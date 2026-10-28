# 패처 빌드와 번역 자료

`src/Patcher.cs`는 Windows Forms 패처이며 `src/PatcherTests.cs`는 임시 게임 폴더를 사용하는 시험 프로그램입니다. 게임 설치 폴더에서 시험하지 않습니다.

## 빌드

1. 이 저장소를 내려받습니다.
2. 정식 Release ZIP의 `payload/dropzone` 폴더를 저장소의 `payload/dropzone`으로 복사합니다. `payload/manifest.json`과 같은 버전의 파일을 사용하세요.
3. Windows PowerShell에서 다음 명령을 실행합니다. Windows에 포함된 .NET Framework 4.x C# 컴파일러를 사용합니다.

```powershell
.\build.ps1 -RunTests -RealPayloadTest
```

완성된 프로그램은 `build/AI 자동 번역 패치.exe`입니다. 실제 사용 시 EXE 옆에 같은 버전의 `payload` 폴더를 두세요. 실행 파일의 `--verify-package` 옵션은 패치 파일의 SHA-256을 읽기 전용으로 검사하며 성공 시 종료 코드 0을 반환합니다.

배포 파일 없이 적용·복원 로직만 시험하려면 다음 명령을 사용합니다.

```powershell
.\build.ps1 -TestOnly -RunTests
```

이 명령은 실제 게임에 적용할 실행 파일을 만들지 않습니다. 시험은 임시 폴더에서만 실행됩니다.

## 번역 자료

`translations/ko.jsonl`은 문자열 해시, ID 이름과 새 한국어 번역을 담습니다. `note`가 있으면 원문 오류나 수치 표기 같은 검토 근거이며, 게임 화면에는 삽입하지 않습니다. 게임 리소스에는 원래 영어 슬롯을 대상으로 번역을 넣습니다.

`validation`에는 문자열 구조·원문 버전·폰트 커버·적용 및 복원 검사 결과를 보관합니다. 이 자료는 정적 검사와 실행 검증의 범위를 구분합니다.

Release에 포함된 게임 리소스를 개별 편집하면 패처의 무결성 검사에 통과하지 못합니다. 번역 수정 제안은 이슈 또는 번역 자료 변경으로 남겨 주세요.

## 배포 파일

사용자용 ZIP에는 패처 EXE, `payload/dropzone`의 번역 파일과 글꼴, 설명서와 글꼴 라이선스를 함께 넣습니다. `patcher-tests.exe`와 시험용 폴더는 포함하지 않습니다. 버전은 `payload/manifest.json`에서 관리합니다.
