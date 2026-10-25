# CAD 원본 보관 폴더

이 폴더의 파일은 제조사 라이선스상 재배포할 수 없어 저장소에 포함하지 않습니다 (`.gitignore` 처리).

## 받는 방법

1. mk Technology Group CAD 다운로드 페이지에서 벨트 컨베이어 **GUF-P 2000** STEP 파일을 받습니다.
   https://www.mk-group.com/en/service/download-center/cad-data.html
2. 받은 `.step` / `.stp` 파일을 이 폴더에 둡니다.
3. 변환과 Unity 임포트 절차는 [docs/cad-pipeline.md](../docs/cad-pipeline.md)를 따릅니다 (`tools/cad/export_conveyor.py` 한 번 실행).

> 사용 전 mk 사이트의 CAD 데이터 이용 조건을 직접 확인하세요.

## ABB IRB 2600-20/1.65 로봇 (2단계)

1. ABB Library에서 문서 ID **9AKK107492A4717** ("CAD Model, STEP, IRB 2600, Type C, 20-165, rev00", ZIP 7.8MB)을 받습니다.
   https://library.abb.com/r?dk=drawing&q=IRB%202600
2. ZIP 안의 STEP 12개를 `cad/irb2600/`에 풉니다 (하위 폴더 없이).
3. 구조 분석과 변환 절차는 [docs/cad-robot.md](../docs/cad-robot.md)를 따릅니다.

> 사용 전 ABB Library의 이용 조건(Terms & Conditions)을 직접 확인하세요.
