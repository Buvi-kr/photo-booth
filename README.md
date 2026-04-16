# 🌌 포천아트밸리 천문과학관 무인 포토부스 시스템

포천아트밸리 천문과학관에 최적화된 데이터 기반 무인 포토부스 시스템입니다. 크로마키 합성, 고화질 4K 웹캠 제어, 그리고 현장에서의 유연한 설정을 지원합니다.

## ✨ 주요 기능

- **데이터 기반 아키텍처:** `config.json` 수정만으로 배경 추가/삭제 및 설정 변경 가능 (유니티 재빌드 불필요)
- **4K 웹캠 최적화:** 고해상도 웹캠 입력을 지원하며, 인물의 위치(Zoom, Move) 및 톤(Color Grading)을 배경별로 개별 보정 가능
- **정밀 크로마키:** 셰이더 기반의 실시간 크로마키 합성과 배경별 개별 크로마키 수치(Override) 지원
- **통합 관리자 모드:** `Ctrl + Alt + S` 단축키를 통해 현장에서 실시간으로 크로마키 및 레이아웃 캘리브레이션 가능
- **StreamingAssets 로딩:** 모든 영상 및 이미지를 `StreamingAssets`에서 직접 로드하여 빌드 후에도 리소스 교환 가능

## 🛠 실행 및 설정 안내

1. **에디터 설정:**
   - 유니티 에디터 상단 메뉴에서 `PhotoBooth > 👑 올인원: 전체 시스템 자동 세팅`을 클릭하면 모든 UI와 스크립트 참조가 자동으로 연결됩니다.
2. **배경 리소스 추가:**
   - `StreamingAssets/` 폴더에 배경 이미지(`.jpg`)를 넣고 `config.json`의 `backgrounds` 리스트에 파일명(확장자 제외)을 등록하면 시스템이 자동으로 인식합니다.
3. **관리자 모드:**
   - 실행 중 `Ctrl + Alt + S`를 입력하여 설정 메뉴를 호출할 수 있으며, 변경된 사항은 '저장' 버튼을 통해 `config.json`에 즉시 기록됩니다.

## 📁 프로젝트 구조

- `Assets/Scripts/Core`: 핵심 상태 관리 및 크로마키 로직 (`AppStateManager`, `ChromaKeyController`)
- `Assets/Scripts/Config`: JSON 데이터 로딩 및 모델 (`PhotoBoothConfigLoader`, `PhotoBoothData`)
- `Assets/Editor`: 에디터 자동화 스크립트 (`MasterSetupBuilder`)
- `Assets/StreamingAssets`: 실행 시 참조되는 외부 설정 및 영상/이미지 리소스
