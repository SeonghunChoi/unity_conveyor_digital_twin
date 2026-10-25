# 로봇 CAD (ABB IRB 2600-20/1.65)

2단계 로봇 셀의 로봇 모델. 1단계 컨베이어와 같은 파이프라인(STEP → FreeCAD → glb → glTFast)을 쓴다 ([cad-pipeline.md](cad-pipeline.md)).

## 1. 원본

| 항목 | 내용 |
| --- | --- |
| 출처 | ABB Library, 문서 ID **9AKK107492A4717** "CAD Model, STEP, IRB 2600, Type C, 20-165, rev00" (공개, 로그인 불필요) |
| 받는 위치 | `cad/irb2600/` — 원본 ZIP과 풀어 놓은 STEP 12개 (`.gitignore` 대상, 커밋 금지) |
| 형식 | STEP AP214, SolidWorks 2016에서 내보냄, 단위 mm |
| 구성 | 링크별 파일: `BASE`, `LINK1`~`LINK6`, 케이블 4개(`CABLES_LINK1a/1b/2/3`), `WORKING-SPACE`(작업 영역 외곽) |

컨베이어 STEP은 조립 트리 하나에 부품 수백 개가 섞여 있어 역할별로 다시 묶어야 했지만,
로봇은 **ABB가 이미 관절 단위로 파일을 나눠 두었다.** 파일 하나가 곧 Unity의 관절 노드 하나다.

## 2. 구조 분석 (`tools/cad/inspect_robot.py`)

```bash
"C:/Users/seong/AppData/Local/Programs/FreeCAD 1.1/bin/FreeCADCmd.exe" tools/cad/inspect_robot.py   # → cad/irb2600/inspect.json
```

모든 파일이 **로봇 베이스 원점·홈 자세(모든 관절 0°)** 의 같은 좌표계에 있다. STEP 좌표는 Z-up, 팔은 +X 방향으로 뻗어 있다.

### 링크별 형상 (정밀 바운딩 박스, mm)

| 파일 | 솔리드 | 면 | min | max |
| --- | --- | --- | --- | --- |
| BASE | 1 | 973 | (−401, −257, 0) | (275, 257, 232) |
| LINK1 | 1 | 1119 | (−264, −150, 154) | (280, 272, 608) |
| LINK2 | 1 | 444 | (47, −238, 342) | (253, −88, 1215) |
| LINK3 | 1 | 717 | (11, −150, 1031) | (455, 191, 1382) |
| LINK4 | 3 | 1268 | (438, −88, 1183) | (1021, 79, 1337) |
| LINK5 | 1 | 150 | (888, −21, 1203) | (1002, 21, 1317) |
| LINK6 | 1 | 39 | (996, −32, 1228) | (1030, 32, 1292) |

일반 `BoundBox`는 B-spline 제어점을 포함해 LINK1이 x=883, z=−536까지 부풀려 나온다. 치수 확인에는 `optimalBoundingBox()`를 쓴다.

### 관절 축 (인접 링크의 동축 원통면에서 추출)

관절에서는 두 링크가 같은 회전축의 베어링·하우징을 공유하므로 양쪽에 동축 원통면이 생긴다. 이를 찾아 축선을 얻었다.

| 관절 | 부모 → 자식 | 축 방향 (STEP) | 축 위 점 (mm) | 의미 |
| --- | --- | --- | --- | --- |
| J1 | BASE → LINK1 | Z | (0, 0, 0) | 베이스 회전 |
| J2 | LINK1 → LINK2 | Y | (150, 0, 445) | 하부 암 (어깨) |
| J3 | LINK2 → LINK3 | Y | (150, 0, 1145) | 상부 암 (팔꿈치) |
| J4 | LINK3 → LINK4 | X | (·, 0, 1260) | 상부 암 비틀기 |
| J5 | LINK4 → LINK5 | Y | (945, 0, 1260) | 손목 굽힘 |
| J6 | LINK5 → LINK6 | X | (·, 0, 1260) | 플랜지 회전 |

여기서 나오는 링크 치수 — J1→J2 오프셋 150 / 어깨 높이 445 / 하부 암 700 / 팔꿈치 오프셋 115 / 상부 암 795 / 손목→플랜지 85 —
는 ABB가 공개한 IRB 2600-20/1.65 기구학 치수와 일치한다. 플랜지 면은 x=1030(= 945 + 85)이고,
최대 도달거리 150 + 700 + 795 = 1645mm가 제품명의 1.65m다.

## 3. 변환 (`tools/cad/export_robot.py`, 2일 차)

```bash
"C:/Users/seong/AppData/Local/Programs/FreeCAD 1.1/bin/FreeCADCmd.exe" tools/cad/export_robot.py          # → cad/irb2600.glb, Twin/Assets/ThirdParty/Cad/
"C:/Users/seong/AppData/Local/Programs/FreeCAD 1.1/bin/python.exe" -I tools/cad/inspect_glb.py cad/irb2600.glb
```

- 노드 계층: `Robot/Base/J1/J2/J3/J4/J5/J6`, 각 노드 아래에 `Jn_Mesh`.
  - 각 관절 노드의 원점은 축 위의 점이다: J1 (0,0,0), J2 (150,0,445), J3 (150,0,1145), J4 (150,0,1260) — J3 바로 위 축선,
    J5 (945,0,1260), J6 (1030,0,1260) — 플랜지 면. 그래서 그리퍼는 J6 노드에 바로 붙인다.
  - 메시 노드는 관절 원점만큼 되돌린 오프셋을 가진다 (OCCT writer가 형상 Placement를 메시 노드 변환으로 내보냄). 동작에는 영향이 없다.
- 케이블은 붙어 있는 링크에 합쳤다 (`CABLES_LINK1a/1b` → J1, `CABLES_LINK2` → J2, `CABLES_LINK3` → J3).
  관절을 지나며 휘는 부품이라 큰 각도에서 어색하면 시연 때 뺀다.
- `WORKING-SPACE`는 메시로 넣지 않는다 (셀 배치 때 도달 범위 점검용).
- primitive 병합은 컨베이어와 같은 `tools/cad/gltf_merge.py`를 쓴다 (6,302 → 7).
- 결과: 메시 7개, 약 24.8만 삼각형, 6.2MB. 컨베이어(9.9만)보다 많지만 PC 시연에는 문제없는 수준이다.

## 4. Unity 점검 (`Tools/CAD/Check Robot Import`)

`Twin/Assets/Editor/RobotImportCheck.cs`가 (1) 홈 자세 관절 위치, (2) 관절별 회전 부호, (3) 복합 자세 3개를 STEP 좌표의
오른손 정기구학(FK)과 비교한다. 2026-10-26 결과: **모두 0.5mm 이내로 통과**.

### 좌표 변환

STEP (x, y, z) mm → Unity (−x, z, −y) / 1000. 홈 자세에서 팔은 Unity **−X** 방향으로 뻗는다.

### 관절 규약 (트윈이 그대로 쓸 값)

`joint.localRotation = Quaternion.AngleAxis(θ, 축)` 에서 θ는 서버가 보낸 관절각(도, ABB 부호), 축은 아래 표:

| 관절 | STEP 축 | Unity 축 (양의 θ) |
| --- | --- | --- |
| J1 | +Z | (0, −1, 0) |
| J2 | +Y | (0, 0, +1) |
| J3 | +Y | (0, 0, +1) |
| J4 | +X | (+1, 0, 0) |
| J5 | +Y | (0, 0, +1) |
| J6 | +X | (+1, 0, 0) |

STEP→Unity 변환이 거울 반전(행렬식 −1)이라, STEP 축을 그대로 옮긴 Unity 축으로는 **모든 관절이 반대로 돈다** (측정으로 확인).
그래서 위 표의 축은 옮긴 축의 반대 방향이다.

- 가정: ABB 관절 양의 방향 = 베이스 좌표(STEP 좌표와 같음)에서 오른손 법칙 (J1 위에서 볼 때 반시계, J2 팔 앞으로, J3 팔 아래로).
  더미 서버가 같은 규약으로 각도를 만들므로 트윈 동작은 일관되지만, ABB 실기·RobotStudio와의 대조는 하지 않았다.
- 관절 가동 범위(ABB 사양서)는 4일 차 서버 가상 컨트롤러에서 적용한다.
