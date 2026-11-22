# TokenViewer for Windows

**Codex (OpenAI)**, **Claude (Anthropic)**, **Gemini (Google)** 구독 잔여량을 Windows 작업 표시줄 트레이에서 모니터링하는 C# / WPF 앱입니다. macOS Swift 앱(`../Sources`)과 기능은 같고, 코드는 별도입니다.

## 요구 사항

- Windows 10 1809 이상 / Windows 11 (x64 또는 ARM64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (빌드할 때만 필요하고, 배포한 exe는 런타임을 포함합니다)
- Microsoft Edge WebView2 런타임 (Windows 11에는 기본 설치되어 있습니다. 없으면 [Evergreen 런타임](https://go.microsoft.com/fwlink/p/?LinkId=2124703)을 설치하세요)

## 빌드 및 실행

```powershell
cd windows
dotnet test tests/TokenViewer.Core.Tests        # 파서/로직 테스트
dotnet run --project src/TokenViewer.App        # 개발 실행
./scripts/publish.ps1                           # dist/win-x64/TokenViewer.exe (단일 파일)
./scripts/publish.ps1 -Runtime win-arm64        # ARM64용
```

macOS에서는 Core 테스트를 실행하고 WPF 프로젝트를 컴파일하는 데까지만 할 수 있습니다(`EnableWindowsTargeting`). 실행은 Windows에서만 됩니다.

## 구조

```
windows/
  src/TokenViewer.Core/    UI 비의존 로직 (net10.0): 모델, 3개 프로바이더, 파서, OAuth, 알림 판정
  src/TokenViewer.App/     WPF 앱 (net10.0-windows): 트레이, 팝업, 설정, 로그인 창, Windows 서비스
  tests/TokenViewer.Core.Tests/
```

| macOS | Windows |
|---|---|
| `NSStatusItem` 메뉴바 | 프로바이더별 트레이 아이콘 (H.NotifyIcon) |
| 숨김 `WKWebView` fetch | 숨김 WebView2 컨트롤러 + `chrome.webview.postMessage` |
| Keychain | Credential Manager (`TokenViewer:<account>`) |
| `UserDefaults` | `%APPDATA%\TokenViewer\settings.json`, `state.json` |
| WKWebsiteDataStore | `%LOCALAPPDATA%\TokenViewer\WebView2` |
| `UNUserNotification` | 트레이 알림 (Windows 토스트로 표시) |
| `SMAppService` | `HKCU\...\CurrentVersion\Run` |
| `ps` / `lsof` | WMI `Win32_Process` / `GetExtendedTcpTable` |

## macOS 버전과 다른 점

- **트레이 표시 방식**: 트레이 아이콘은 작은 정사각형이라 메뉴바처럼 가로로 긴 텍스트를 넣을 수 없습니다. 그래서 프로바이더마다 아이콘을 하나씩 두고, 원형 게이지 / 큰 숫자 / 상태 도트 세 가지 모양을 지원합니다. 자세한 수치와 리셋 시간은 툴팁과 클릭 팝업에서 보여줍니다.
- **아이콘 순서**: 앱은 설정한 순서대로 아이콘을 만들지만, 실제 위치는 Windows가 정합니다. 처음에는 숨겨진 아이콘(^) 영역에 들어갈 수 있으니, 설정 → 개인 설정 → 작업 표시줄 → 기타 시스템 트레이 아이콘에서 TokenViewer를 켜 두세요.
- **User-Agent**: Safari로 위장하지 않고 WebView2 기본(Edge) UA를 그대로 씁니다.
- **Google 계정으로 Claude 로그인**: Google이 임베디드 브라우저 로그인을 막을 수 있습니다. 그럴 때는 이메일 로그인을 쓰거나, 설정의 "고급: 수동 토큰/세션 쿠키 입력"에 `sessionKey`를 넣으세요.
