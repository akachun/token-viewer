# TokenViewer

macOS 메뉴바(상태바)에서 **Codex (OpenAI)**, **Claude (Anthropic)**, **Gemini (Google)** 구독 잔여량을 독립적으로 인증·모니터링하는 네이티브 Swift/SwiftUI 앱입니다.

## 주요 기능

- **앱 내 독립 인증** (로컬 CLI 비의존)
  - **Codex (`chatgpt.com`)**: 앱 내 `WKWebView` 로그인 + 동일 브라우저 세션 컨텍스트로 `/api/auth/session` 및 `/backend-api/wham/usage` 조회 (수동 Access Token 폴백 지원)
  - **Claude (`claude.ai`)**: 앱 내 `WKWebView` 로그인 + `/api/organizations` 및 `/api/organizations/{id}/usage` 조회 (수동 `sessionKey` 폴백 지원)
  - **Gemini (Google)**: 시스템 브라우저 OAuth 2.0 + PKCE 로컬 콜백 서버(`127.0.0.1`) 로그인 및 `cloudcode-pa.googleapis.com` 쿼터 조회 (수동 Access Token 지원)
- **상태바 커스터마이징**
  - AI별 표시 ON/OFF 및 순서 변경
  - 5가지 표시 모양: 텍스트 퍼센트, 미니 프로그레스 바, 원형 게이지 링, 상태 도트 + 수치, 아이콘 + 수치
  - 잔여량(`Remaining %`) ↔ 사용량(`Used %`) 전환 및 리셋까지 남은 시간 표시
  - 색상 테마: 상태 색상(초록/노랑/빨강), 브랜드 색상, 모노크롬(시스템 메뉴바 자동 적응)
  - 설정 창 내 실시간 다크/라이트 메뉴바 미리보기
- **알림 및 시스템 연동**
  - 잔여량 임계치 도달 시 macOS 로컬 알림 (윈도우별 중복 알림 방지)
  - 자동 갱신 주기 설정 (1분 ~ 30분) 및 로그인 시 자동 실행 (`SMAppService`)
  - 데모 데이터 모드 (로그인 전 UI 및 알림 즉시 테스트)

## 빌드 및 실행

```bash
cd token-viewer
chmod +x scripts/build_app.sh
./scripts/build_app.sh
open dist/TokenViewer.app
```

또는 개발 중 바로 실행:

```bash
cd token-viewer
swift run
```

## Windows 버전

`windows/` 디렉터리에 C# / WPF로 만든 Windows 트레이 앱이 있습니다. 빌드 방법과 macOS 버전과의 차이는 [windows/README.md](windows/README.md)를 참고하세요.
