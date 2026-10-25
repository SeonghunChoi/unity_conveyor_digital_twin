# -*- coding: utf-8 -*-
"""
ABB IRB 2600-20/1.65 STEP(링크별 파일) → 관절 계층 glb 변환 스크립트.

실행 (저장소 루트에서):
    "C:/Users/<user>/AppData/Local/Programs/FreeCAD 1.1/bin/FreeCADCmd.exe" tools/cad/export_robot.py

입력: cad/irb2600/*.STEP   출력: cad/irb2600.glb   (둘 다 제조사 자료라 커밋 금지)

처리:
 1. 링크별 STEP을 읽는다 (모두 베이스 원점·홈 자세의 같은 좌표계, mm, Z-up)
 2. 케이블 파일은 붙어 있는 링크에 합친다
 3. 관절 노드를 중첩(Base/J1/J2/.../J6)하고, 각 노드 원점을 관절 축 위의 점에 둔다.
    메시는 그 원점 기준으로 옮겨 넣으므로, 트윈은 노드의 로컬 회전만 바꾸면 관절이 축을 따라 돈다.
 4. 테셀레이션 후 glb 내보내기, 메시별 primitive 병합 (gltf_merge.py), Twin/Assets/ThirdParty/Cad/ 로 복사

관절 축은 tools/cad/inspect_robot.py로 인접 링크의 동축 원통면에서 뽑은 값이다 (docs/cad-robot.md).
결과 점검: python tools/cad/inspect_glb.py cad/irb2600.glb
"""
import glob
import os
import shutil
import sys

import FreeCAD
import Import
import MeshPart
import Part

# ---------- 경로 ----------
try:
    _here = os.path.dirname(os.path.abspath(__file__))
    ROOT = os.path.abspath(os.path.join(_here, "..", ".."))
except NameError:  # FreeCADCmd 실행 방식에 따라 __file__이 없을 수 있음
    ROOT = os.getcwd()
    _here = os.path.join(ROOT, "tools", "cad")
sys.path.insert(0, _here)
from gltf_merge import merge_primitives  # noqa: E402  (공용 glb 후처리)

SRC_DIR = os.path.join(ROOT, "cad", "irb2600")
DST = os.path.join(ROOT, "cad", "irb2600.glb")
UNITY_ASSETS = os.path.join(ROOT, "Twin", "Assets")
UNITY_CAD_DIR = os.path.join(UNITY_ASSETS, "ThirdParty", "Cad")

# ---------- 테셀레이션 정밀도 (컨베이어와 같음) ----------
LINEAR_DEFLECTION_MM = 0.5
ANGULAR_DEFLECTION_RAD = 0.5

# ---------- 관절 정의 ----------
# (노드 이름, 메시에 넣을 파일 키, 관절 원점 STEP 좌표 mm)
# 원점은 축 위의 아무 점이나 되지만, 읽기 쉽게 DH식 위치를 골랐다:
#   J4는 J3 바로 위 축선(x=150), J6은 플랜지 면(x=1030)이라 그리퍼를 J6에 바로 붙일 수 있다.
# 축 방향(STEP): J1 Z, J2 Y, J3 Y, J4 X, J5 Y, J6 X — 회전 방향은 Unity 쪽 점검에서 확정한다.
CHAIN = [
    ("Base", ["BASE"], (0.0, 0.0, 0.0)),
    ("J1", ["LINK1", "CABLES_LINK1A", "CABLES_LINK1B"], (0.0, 0.0, 0.0)),
    ("J2", ["LINK2", "CABLES_LINK2"], (150.0, 0.0, 445.0)),
    ("J3", ["LINK3", "CABLES_LINK3"], (150.0, 0.0, 1145.0)),
    ("J4", ["LINK4"], (150.0, 0.0, 1260.0)),
    ("J5", ["LINK5"], (945.0, 0.0, 1260.0)),
    ("J6", ["LINK6"], (1030.0, 0.0, 1260.0)),
]
KEYS = ["CABLES_LINK1A", "CABLES_LINK1B", "CABLES_LINK2", "CABLES_LINK3", "WORKING-SPACE",
        "BASE", "LINK1", "LINK2", "LINK3", "LINK4", "LINK5", "LINK6"]


def key_of(path):
    name = os.path.basename(path).upper()
    for k in KEYS:
        if "_" + k + "_" in name:
            return k
    return None


def main():
    files = {key_of(p): p for p in glob.glob(os.path.join(SRC_DIR, "*.STEP"))}
    missing = [k for _, keys, _ in CHAIN for k in keys if k not in files]
    if missing:
        raise SystemExit("STEP 파일이 없습니다: %s (cad/README.md 참고)" % ", ".join(missing))

    doc = FreeCAD.newDocument("irb2600")
    root = doc.addObject("App::Part", "Robot")
    root.Label = "Robot"

    parent, parent_origin = root, FreeCAD.Vector(0, 0, 0)
    report = []
    for name, keys, origin_mm in CHAIN:
        origin = FreeCAD.Vector(*origin_mm)
        shapes = []
        for k in keys:
            s = Part.Shape()
            s.read(files[k])
            shapes.append(s)
        comp = Part.makeCompound(shapes)
        # 메시를 관절 원점 기준으로 옮긴다 (홈 자세에서 모든 회전은 0이므로 평행 이동만 하면 된다)
        comp.translate(origin.negative())

        node = doc.addObject("App::Part", name)
        node.Label = name
        node.Placement = FreeCAD.Placement(origin - parent_origin, FreeCAD.Rotation())
        parent.addObject(node)

        mesh = MeshPart.meshFromShape(Shape=comp, LinearDeflection=LINEAR_DEFLECTION_MM,
                                      AngularDeflection=ANGULAR_DEFLECTION_RAD, Relative=False)
        feat = doc.addObject("Part::Feature", name + "_Mesh")
        feat.Label = name + "_Mesh"
        feat.Shape = comp
        node.addObject(feat)

        report.append("%-4s files=%-40s origin=(%6.1f, %6.1f, %6.1f) mm  tris=%6d" % (
            name, "+".join(keys), origin.x, origin.y, origin.z, mesh.CountFacets))
        parent, parent_origin = node, origin

    doc.recompute()

    if os.path.exists(DST):
        os.remove(DST)
    Import.export([root], DST)
    merged = merge_primitives(DST)

    for line in report:
        print(line)
    print("primitives merged: %d -> %d" % merged)
    print("-> " + DST)

    if os.path.isdir(UNITY_ASSETS):
        os.makedirs(UNITY_CAD_DIR, exist_ok=True)
        shutil.copyfile(DST, os.path.join(UNITY_CAD_DIR, os.path.basename(DST)))
        print("-> " + UNITY_CAD_DIR)


main()
