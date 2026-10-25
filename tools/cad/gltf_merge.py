# -*- coding: utf-8 -*-
"""
glb 후처리 공용 함수 (export_conveyor.py, export_robot.py가 함께 쓴다).
FreeCAD 없이 표준 라이브러리만 쓴다.
"""
import json
import struct


# ---------- glb 후처리: 메시별 primitive 병합 ----------
# OCCT glTF writer는 B-rep 면 하나마다 primitive를 만든다 (Frame만 1,700여 개).
# glTFast는 primitive를 서브메시로 가져오므로 그대로 두면 서브메시 수만큼 드로우콜이 생긴다.
# 머티리얼이 없어 나눌 이유가 없으므로 메시마다 primitive 하나로 합친다.
_COMP = {5120: "b", 5121: "B", 5122: "h", 5123: "H", 5125: "I", 5126: "f"}
_NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}


def _read_accessor(gltf, binchunk, idx):
    a = gltf["accessors"][idx]
    bv = gltf["bufferViews"][a["bufferView"]]
    fmt = _COMP[a["componentType"]]
    n = _NCOMP[a["type"]]
    size = struct.calcsize("<" + fmt)
    stride = bv.get("byteStride", size * n)
    base = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    out = []
    for i in range(a["count"]):
        out.extend(struct.unpack_from("<%d%s" % (n, fmt), binchunk, base + i * stride))
    return out


def merge_primitives(path):
    with open(path, "rb") as f:
        data = f.read()
    json_len = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20:20 + json_len])
    bin_off = 20 + json_len
    bin_len = struct.unpack_from("<I", data, bin_off)[0]
    binchunk = data[bin_off + 8:bin_off + 8 + bin_len]
    if gltf.get("images") or gltf.get("textures"):
        raise SystemExit("텍스처가 있는 glb는 병합을 지원하지 않습니다")

    new_buf = bytearray()
    views, accessors = [], []
    before = after = 0

    def add(values, comp_type, type_name, target, minmax=False):
        fmt = _COMP[comp_type]
        while len(new_buf) % 4:
            new_buf.append(0)
        off = len(new_buf)
        new_buf.extend(struct.pack("<%d%s" % (len(values), fmt), *values))
        views.append({"buffer": 0, "byteOffset": off, "byteLength": len(new_buf) - off, "target": target})
        n = _NCOMP[type_name]
        acc = {"bufferView": len(views) - 1, "componentType": comp_type,
               "count": len(values) // n, "type": type_name}
        if minmax:
            acc["min"] = [min(values[i::n]) for i in range(n)]
            acc["max"] = [max(values[i::n]) for i in range(n)]
        accessors.append(acc)
        return len(accessors) - 1

    for mesh in gltf["meshes"]:
        prims = mesh["primitives"]
        before += len(prims)
        keys = sorted(prims[0]["attributes"].keys())
        attrs = {k: [] for k in keys}
        indices = []
        base = 0
        for p in prims:
            if sorted(p["attributes"].keys()) != keys or p.get("mode", 4) != 4:
                raise SystemExit("primitive 구성이 달라 병합할 수 없습니다: " + mesh.get("name", "?"))
            for k in keys:
                attrs[k].extend(_read_accessor(gltf, binchunk, p["attributes"][k]))
            count = gltf["accessors"][p["attributes"]["POSITION"]]["count"]
            if "indices" in p:
                indices.extend(i + base for i in _read_accessor(gltf, binchunk, p["indices"]))
            else:
                indices.extend(range(base, base + count))
            base += count

        merged = {"attributes": {}, "mode": 4}
        for k in keys:
            src = gltf["accessors"][prims[0]["attributes"][k]]
            merged["attributes"][k] = add(attrs[k], src["componentType"], src["type"], 34962, k == "POSITION")
        merged["indices"] = add(indices, 5125 if base > 65535 else 5123, "SCALAR", 34963)
        if "material" in prims[0]:
            merged["material"] = prims[0]["material"]
        mesh["primitives"] = [merged]
        after += 1

    gltf["accessors"] = accessors
    gltf["bufferViews"] = views
    gltf["buffers"] = [{"byteLength": len(new_buf)}]

    js = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    js += b" " * ((4 - len(js) % 4) % 4)
    while len(new_buf) % 4:
        new_buf.append(0)
    total = 12 + 8 + len(js) + 8 + len(new_buf)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(new_buf), 0x004E4942) + bytes(new_buf))
    return before, after

