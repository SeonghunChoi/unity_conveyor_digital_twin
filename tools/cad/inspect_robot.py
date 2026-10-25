# -*- coding: utf-8 -*-
"""
ABB IRB 2600-20/1.65 STEP(링크별 파일) 구조 분석 스크립트.

실행 (저장소 루트에서):
    "C:/Users/<user>/AppData/Local/Programs/FreeCAD 1.1/bin/FreeCADCmd.exe" tools/cad/inspect_robot.py

입력: cad/irb2600/*.STEP  (ABB Library 9AKK107492A4717, 커밋 금지)
출력: 콘솔 + cad/irb2600/inspect.json

하는 일:
 1. 파일별 솔리드 수, 바운딩 박스(mm), 부피를 출력한다.
 2. 인접 링크(BASE-LINK1, LINK1-LINK2, ...) 사이에서 같은 축 위에 있는 원통면을 찾아 관절 축 후보를 낸다.
    관절은 두 링크가 같은 회전축의 베어링·하우징을 공유하므로, 양쪽에 동축 원통면이 생긴다.
"""
import glob
import json
import math
import os

import FreeCAD
import Part

try:
    _here = os.path.dirname(os.path.abspath(__file__))
    ROOT = os.path.abspath(os.path.join(_here, "..", ".."))
except NameError:
    ROOT = os.getcwd()

SRC_DIR = os.path.join(ROOT, "cad", "irb2600")
OUT = os.path.join(SRC_DIR, "inspect.json")

CHAIN = ["BASE", "LINK1", "LINK2", "LINK3", "LINK4", "LINK5", "LINK6"]
MIN_RADIUS_MM = 15.0      # 이보다 작은 원통(볼트 구멍 등)은 무시
AXIS_TOL_MM = 1.0         # 두 축선 사이 거리 허용치
DIR_TOL = 1e-3            # 방향 벡터 평행 허용치


def key_of(path):
    name = os.path.basename(path).upper()
    for k in ["CABLES_LINK1A", "CABLES_LINK1B", "CABLES_LINK2", "CABLES_LINK3", "WORKING-SPACE"] + CHAIN:
        if "_" + k + "_" in name or "_" + k in name.replace(".STEP", "_"):
            return k
    return name


def load(path):
    shape = Part.Shape()
    shape.read(path)
    return shape


def cylinders(shape):
    out = []
    for f in shape.Faces:
        s = f.Surface
        if isinstance(s, Part.Cylinder) and s.Radius >= MIN_RADIUS_MM:
            d = FreeCAD.Vector(s.Axis).normalize()
            # 방향 부호 통일 (가장 큰 성분이 양수)
            comps = [d.x, d.y, d.z]
            i = max(range(3), key=lambda j: abs(comps[j]))
            if comps[i] < 0:
                d = d * -1
            out.append((FreeCAD.Vector(s.Center), d, s.Radius, f.Area))
    return out


def line_distance(p1, d1, p2):
    v = p2 - p1
    return (v - d1 * v.dot(d1)).Length


def common_axes(ca, cb):
    """두 링크의 원통면에서 동축 쌍을 모아 축선별로 묶는다"""
    groups = []
    for pa, da, ra, aa in ca:
        for pb, db, rb, ab in cb:
            if (da - db).Length > DIR_TOL:
                continue
            if line_distance(pa, da, pb) > AXIS_TOL_MM:
                continue
            for g in groups:
                if (g["dir"] - da).Length < DIR_TOL and line_distance(g["point"], g["dir"], pa) < AXIS_TOL_MM:
                    g["radii"].add(round(ra, 1)); g["radii"].add(round(rb, 1)); g["area"] += aa + ab
                    break
            else:
                groups.append({"point": pa, "dir": da, "radii": {round(ra, 1), round(rb, 1)}, "area": aa + ab})
    groups.sort(key=lambda g: -g["area"])
    return groups


def axis_point_closest_to_origin(p, d):
    # 축선 위에서 원점에 가장 가까운 점 (축 위치를 비교하기 쉽게)
    return p - d * p.dot(d)


def fmt(v):
    return "(%.1f, %.1f, %.1f)" % (v.x, v.y, v.z)


def main():
    files = sorted(glob.glob(os.path.join(SRC_DIR, "*.STEP")))
    shapes, report = {}, {"files": {}, "joints": []}
    print("== 파일별 형상 (단위 mm, STEP 좌표 그대로) ==")
    for path in files:
        k = key_of(path)
        s = load(path)
        shapes[k] = s
        bb = s.BoundBox
        info = {
            "file": os.path.basename(path),
            "solids": len(s.Solids),
            "faces": len(s.Faces),
            "bbox_min": [bb.XMin, bb.YMin, bb.ZMin],
            "bbox_max": [bb.XMax, bb.YMax, bb.ZMax],
            "volume_cm3": s.Volume / 1000.0 if s.Solids else 0.0,
        }
        report["files"][k] = info
        print("%-15s solids=%3d faces=%5d  min=%s max=%s  vol=%.0fcm3" % (
            k, info["solids"], info["faces"], fmt(FreeCAD.Vector(*info["bbox_min"])),
            fmt(FreeCAD.Vector(*info["bbox_max"])), info["volume_cm3"]))

    print("\n== 인접 링크 사이 동축 원통면 (관절 축 후보, 면적 큰 순) ==")
    cyl = {k: cylinders(shapes[k]) for k in CHAIN if k in shapes}
    for i in range(len(CHAIN) - 1):
        a, b = CHAIN[i], CHAIN[i + 1]
        if a not in cyl or b not in cyl:
            continue
        groups = common_axes(cyl[a], cyl[b])
        print("%s - %s: 후보 %d개" % (a, b, len(groups)))
        for g in groups[:3]:
            p = axis_point_closest_to_origin(g["point"], g["dir"])
            print("   축 방향 %s  축 위 점 %s  반지름 %s  면적 %.0f" % (
                fmt(g["dir"]), fmt(p), sorted(g["radii"]), g["area"]))
        if groups:
            g = groups[0]
            p = axis_point_closest_to_origin(g["point"], g["dir"])
            report["joints"].append({"joint": "J%d" % (i + 1), "parent": a, "child": b,
                                     "dir": [g["dir"].x, g["dir"].y, g["dir"].z],
                                     "point": [p.x, p.y, p.z], "radii": sorted(g["radii"])})

    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    print("\n저장: " + OUT)


main()
