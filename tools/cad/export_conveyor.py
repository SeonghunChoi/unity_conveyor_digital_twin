# -*- coding: utf-8 -*-
"""
mk GUF-P 2000 STEP → 정리된 glb 변환 스크립트.

실행 (저장소 루트에서):
    "C:/Users/<user>/AppData/Local/Programs/FreeCAD 1.1/bin/FreeCADCmd.exe" tools/cad/export_conveyor.py

FreeCADCmd는 추가 인수를 "열 파일"로 해석하므로 경로는 인수로 받지 않고 이 파일 위치 기준으로 정한다.
입력: cad/MK163596.stp   출력: cad/conveyor.glb   (둘 다 제조사 라이선스로 커밋 금지)

처리:
 1. STEP 가져오기
 2. 체결 부품(볼트·너트·T너트·와셔 등) 제거
 3. 남은 부품을 역할별 노드(Belt, Drum_Head, Drum_Tail, DriveMotor, ControlBox, Frame, Legs)로 합침
    - 회전하는 드럼은 노드 원점을 드럼 축 중심에 둔다 (Unity에서 제자리 회전)
 4. 지정한 정밀도로 테셀레이션 후 glb 내보내기
 5. 메시별 primitive를 하나로 병합 (Unity 서브메시·드로우콜 절감), Twin/Assets/ThirdParty/Cad/ 로 복사

결과 점검: python tools/cad/inspect_glb.py cad/conveyor.glb
"""
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

SRC = os.path.join(ROOT, "cad", "MK163596.stp")
DST = os.path.join(ROOT, "cad", "conveyor.glb")
# Unity 프로젝트가 있으면 glb를 복사 (이 경로도 .gitignore 대상)
UNITY_ASSETS = os.path.join(ROOT, "Twin", "Assets")
UNITY_CAD_DIR = os.path.join(UNITY_ASSETS, "ThirdParty", "Cad")

# ---------- 테셀레이션 정밀도 ----------
LINEAR_DEFLECTION_MM = 0.5    # 곡면과 삼각형 사이 최대 오차
ANGULAR_DEFLECTION_RAD = 0.5  # 인접 삼각형 법선 최대 각도

# ---------- 역할 분류 규칙 ----------
# 조립 트리를 내려가며 라벨 접두어가 처음 일치한 규칙의 역할을 하위 부품 전체에 물려준다.
# 순서가 중요하다: 드럼은 구동부(B01) 안에 있으므로 먼저 검사해야 한다.
ROLE_RULES = [
    ("K1029003", "Belt"),          # 벨트 (단일 솔리드 루프)
    ("B91.14.350", "Drum_Head"),   # 구동 드럼 + 구동축
    ("8301CA0283", "Drum_Tail"),   # 텐션 쪽 아이들 드럼 (고정 축 0100BA0300은 Frame)
    ("K101000378", "Drum_Tail"),   # 아이들 드럼 엔드캡(베어링)
    ("K301000985", "Drum_Tail"),
    ("K301000975", "DriveMotor"),  # 감속 모터
    ("EREG230AC", "ControlBox"),   # 속도 조절기(제어 박스)
    ("B67.03.002", "Legs"),        # 다리 2세트
]
DEFAULT_ROLE = "Frame"
ROLE_ORDER = ["Belt", "Drum_Head", "Drum_Tail", "DriveMotor", "ControlBox", "Frame", "Legs"]
PIVOT_AT_CENTER = {"Drum_Head", "Drum_Tail"}


def is_fastener(obj):
    """체결 부품 판별. D*: DIN 표준품, 34.*: T너트, 30mm 미만 K1*/K3*: 캡·와셔류"""
    label = obj.Label
    if label.startswith("D") or label.startswith("34."):
        return True
    bb = obj.Shape.BoundBox
    small = max(bb.XLength, bb.YLength, bb.ZLength) < 30.0
    return small and (label.startswith("K1") or label.startswith("K3"))


def role_of(label, inherited):
    for prefix, role in ROLE_RULES:
        if label.startswith(prefix):
            return role
    return inherited


def children(obj):
    if hasattr(obj, "Group"):
        return list(obj.Group)
    linked = getattr(obj, "LinkedObject", None)
    if linked is not None and linked is not obj and hasattr(linked, "Group"):
        return list(linked.Group)
    return []


def global_shape(obj):
    shape = obj.Shape.copy()
    shape.Placement = obj.getGlobalPlacement()
    return shape


def collect(obj, inherited, buckets, stats):
    role = role_of(obj.Label, inherited)
    kids = children(obj)
    if kids:
        for k in kids:
            collect(k, role, buckets, stats)
        return
    if not hasattr(obj, "Shape") or obj.Shape.isNull():
        return
    if is_fastener(obj):
        stats["removed"] += 1
        return
    buckets.setdefault(role, []).append(global_shape(obj))
    stats["kept"] += 1


def main():
    if not os.path.exists(SRC):
        raise SystemExit("STEP 파일이 없습니다: " + SRC)

    src_doc = FreeCAD.newDocument("mk_src")
    Import.insert(SRC, src_doc.Name)

    buckets, stats = {}, {"kept": 0, "removed": 0}
    for root in src_doc.RootObjects:
        collect(root, DEFAULT_ROLE, buckets, stats)

    out_doc = FreeCAD.newDocument("conveyor")
    root = out_doc.addObject("App::Part", "Conveyor")
    root.Label = "Conveyor"

    report = []
    for role in ROLE_ORDER:
        shapes = buckets.get(role)
        if not shapes:
            report.append("%-11s (없음)" % role)
            continue
        comp = Part.makeCompound(shapes)
        node = out_doc.addObject("App::Part", role)
        node.Label = role
        root.addObject(node)

        if role in PIVOT_AT_CENTER:
            # 드럼 축 중심을 노드 원점으로: 형상을 원점 기준으로 옮기고 노드에 그만큼 위치를 준다
            center = comp.BoundBox.Center
            comp.translate(center.negative())
            node.Placement = FreeCAD.Placement(center, FreeCAD.Rotation())

        # glTF 내보내기는 형상에 이미 저장된 삼각분할을 그대로 쓰고, 없으면 빈 메시를 쓴다.
        # Shape.tessellate()는 각도 허용치를 지정할 수 없어 곡면이 과하게 잘게 나뉘므로(약 8배),
        # MeshPart로 선형·각도 허용치를 모두 지정해 삼각분할을 형상에 미리 채워 둔다.
        mesh = MeshPart.meshFromShape(Shape=comp, LinearDeflection=LINEAR_DEFLECTION_MM,
                                      AngularDeflection=ANGULAR_DEFLECTION_RAD, Relative=False)
        feat = out_doc.addObject("Part::Feature", role + "_Mesh")
        feat.Label = role + "_Mesh"
        feat.Shape = comp
        node.addObject(feat)

        bb = comp.BoundBox
        report.append("%-11s parts=%3d  tris=%6d  size=%.0fx%.0fx%.0f mm" % (
            role, len(shapes), mesh.CountFacets, bb.XLength, bb.YLength, bb.ZLength))

    out_doc.recompute()

    if os.path.exists(DST):
        os.remove(DST)
    Import.export([root], DST)
    merged = merge_primitives(DST)

    print("kept=%d removed(fasteners)=%d" % (stats["kept"], stats["removed"]))
    for line in report:
        print(line)
    print("primitives merged: %d -> %d" % merged)
    print("-> " + DST)

    if os.path.isdir(UNITY_ASSETS):
        os.makedirs(UNITY_CAD_DIR, exist_ok=True)
        shutil.copyfile(DST, os.path.join(UNITY_CAD_DIR, os.path.basename(DST)))
        print("-> " + UNITY_CAD_DIR)


main()
