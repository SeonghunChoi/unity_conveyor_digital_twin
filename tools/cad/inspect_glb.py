# -*- coding: utf-8 -*-
"""
glb 점검: 노드 계층, 메시별 삼각형 수·UV 유무, 전체 크기(m).
외부 패키지 없이 동작하므로 FreeCAD 동봉 python.exe나 일반 Python 어디서든 실행 가능.
    python tools/cad/inspect_glb.py [cad/conveyor.glb]
"""
import json
import struct
import sys

path = sys.argv[1] if len(sys.argv) > 1 else "cad/conveyor.glb"
with open(path, "rb") as f:
    data = f.read()

magic, version, length = struct.unpack_from("<III", data, 0)
assert magic == 0x46546C67, "glb 파일이 아닙니다"
json_len, json_type = struct.unpack_from("<II", data, 12)
gltf = json.loads(data[20:20 + json_len])

print("glTF %d, %.1f MB, generator=%s" % (version, length / 1e6, gltf.get("asset", {}).get("generator")))
acc = gltf.get("accessors", [])
nodes = gltf.get("nodes", [])
meshes = gltf.get("meshes", [])


def mesh_info(mi):
    tris = verts = 0
    uv = normal = True
    lo, hi = [1e9] * 3, [-1e9] * 3
    for prim in meshes[mi]["primitives"]:
        a = prim["attributes"]
        uv &= "TEXCOORD_0" in a
        normal &= "NORMAL" in a
        pos = acc[a["POSITION"]]
        verts += pos["count"]
        lo = [min(x, y) for x, y in zip(lo, pos["min"])]
        hi = [max(x, y) for x, y in zip(hi, pos["max"])]
        tris += (acc[prim["indices"]]["count"] if "indices" in prim else pos["count"]) // 3
    return tris, verts, uv, normal, len(meshes[mi]["primitives"]), lo, hi


total = [0, 0]


def walk(ni, depth):
    n = nodes[ni]
    extra = ""
    if "translation" in n or "matrix" in n or "rotation" in n or "scale" in n:
        extra = "  T=%s R=%s S=%s M=%s" % (n.get("translation"), n.get("rotation"), n.get("scale"),
                                             "yes" if "matrix" in n else None)
    if "mesh" in n:
        t, v, uv, nrm, prims, lo, hi = mesh_info(n["mesh"])
        total[0] += t
        total[1] += v
        extra += "  tris=%d verts=%d prims=%d uv=%s normal=%s  local[%s..%s]" % (
            t, v, prims, uv, nrm, ["%.3f" % x for x in lo], ["%.3f" % x for x in hi])
    print("%s%s%s" % ("  " * depth, n.get("name", "#%d" % ni), extra))
    for c in n.get("children", []):
        walk(c, depth + 1)


scene = gltf["scenes"][gltf.get("scene", 0)]
for r in scene["nodes"]:
    walk(r, 0)
print("TOTAL tris=%d verts=%d meshes=%d materials=%d" % (total[0], total[1], len(meshes), len(gltf.get("materials", []))))
