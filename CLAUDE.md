# KUDURESCUE

Get deleted files back on Windows (NTFS, FAT32, exFAT, Deep scan, disk images, Previous Versions).
Part of the KUDU family of free, open-source (MIT) Windows apps by Azeem Bukhari.

## Working with Azeem (the owner)

- **Branches:** work on `main` and push to `main`. Ask before creating any other branch.
- **New features:** first say what you understood, and ask your doubts and suggestions, one question at a time.
  Only start building when Azeem says "build".
- **Look:** keep the app's current look. No redesigns unless asked.
- **About window credit:** "Made by Azeem, with Claude and ♥" (the heart in the accent colour). Keep it.
- **Commits:** author `Azeem Bukhari <12558389+azeembukhariprivate@users.noreply.github.com>`. Clear messages that say
  what changed and why. Don't put AI model names or versions in commits, code or docs.
- **Words:** the app's text, README and release notes use plain everyday words, not technical jargon.
- **Before pushing:** build and run the tests locally, and re-read the diff. After pushing, watch the GitHub Actions
  build until it's green; fix failures, never skip or disable a test to get green.

## How the app is built

- .NET Framework 4.8 WPF app. The screens are built in C# (no XAML compiler); the themes (`Themes/Light.xaml`,
  `Dark.xaml`, `Styles.xaml`) are XAML loaded at startup as embedded resources, using resource keys such as `Accent`,
  `Text`, `SurfaceBg`. Use the helpers in `Controls/Ui.cs` and existing keys rather than hard-coded colours.
- It builds with the plain .NET SDK on any OS (Microsoft.NETFramework.ReferenceAssemblies); it runs on Windows 10/11.
- Logic that doesn't need Windows lives in `Core/` and is tested by `tests/KUDURESCUE.Tests` (a .NET 8 console program).
- `KUDURESCUE.exe --self-test` drives the real app on Windows (GitHub Actions runs it, keeping screenshots of every window as
  a build download). Add a self-test step for new screens and features.
- Settings live in `HKEY_CURRENT_USER\Software\KUDURESCUE`. The Inno Setup installer (`installer/KUDURESCUE.iss`) installs per
  user, without administrator rights.

```
dotnet run --project tests/KUDURESCUE.Tests -c Release
dotnet build src/KUDURESCUE/KUDURESCUE.csproj -c Release -o out/KUDURESCUE
out\KUDURESCUE\KUDURESCUE.exe --self-test        (Windows only; needs administrator rights)
```

## Shared code (kudu-common)

The files listed in `.kudu-common` (the look and controls in `Controls/`, `Core/Admin.cs`, `AppInfo.cs`, `Args.cs`,
`ThemeManager.cs`, `Unelevated.cs`, `UpdateChecker.cs`, `Views/Dialogs.cs`, `Views/Screenshots.cs`, ...) are copied from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common) and start with a comment saying so.
**Don't edit them here**: change them in kudu-common, then run `python sync.py ../kudurescue` there and commit both repos.
The build fails when a copy differs from kudu-common. App-specific details go in `Core/AppDetails.cs`.

## Releasing

1. Raise `<Version>` in `src/KUDURESCUE/KUDURESCUE.csproj` (and `MyAppVersion` in `installer/KUDURESCUE.iss`).
2. Commit and push to `main`.
3. GitHub Actions (`.github/workflows/build.yml`, Windows) tests and builds the app. When no release exists yet for that
   version it creates the `v` tag and publishes the installer and portable zip on the Releases page, plus a copy named
   `KUDURESCUE-setup.exe` that the README's download button links to (`releases/latest/download/KUDURESCUE-setup.exe`).
   Pushes that keep the same version only build.

## Special rule

- Recovery must never write to the drive being recovered from. Keep the checks that block saving to it.
