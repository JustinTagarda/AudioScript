# Velopack Deployment

This repository is wired for a private-source / public-release GitHub setup:

- Private source repo: your current `AudioTranscript` repository.
- Public release repo: a second GitHub repository that contains only release assets.

The app is configured to:

- check for updates in the background after startup
- download updates automatically
- apply a downloaded update when the app exits

The release build injects the public release repository URL at publish time. Local developer builds intentionally keep updates disabled unless you pass a real release repository URL to the release script.

## One-Time GitHub Setup

1. Create a new public GitHub repository named `AudioTranscript-Releases`.
2. Add a `README.md` so the repo has an initial commit on the `main` branch.
3. Leave the repository otherwise empty. Do not upload source code there.
4. In your private `AudioTranscript` repository, open `Settings` -> `Secrets and variables` -> `Actions`.
5. Add a repository variable named `VELOPACK_RELEASE_REPO_URL`.
6. Set the variable value to the full public release repository URL, for example:

   `https://github.com/YOUR_GITHUB_NAME/AudioTranscript-Releases`

7. Create a GitHub token that can write releases and release assets to the public release repository.
8. Add that token to the private repo Actions secrets as `VELOPACK_RELEASES_TOKEN`.

## Recommended Token Scope

Use the smallest possible scope. The simplest first setup is a fine-grained personal access token that has:

- repository access to `AudioTranscript-Releases`
- `Contents: Read and write`

Do not embed this token in the app. It only belongs in the private repository Actions secret store.

## First Release

1. Update the `<Version>` value in [AudioTranscript.csproj](AudioTranscript.csproj).
2. Commit and push that version change to the private repository.
3. Open the private repository on GitHub.
4. Go to `Actions`.
5. Run the `Publish Velopack Release` workflow manually.
6. Wait for the workflow to finish.
7. Open the public `AudioTranscript-Releases` repository.
8. Confirm a new GitHub release exists with Velopack assets such as `Setup.exe`, `.nupkg`, and `.json`.
9. Use the `Setup.exe` asset from the public release for first-time installs.

## Later Releases

Each time you want to ship a new version:

1. Change the single `<Version>` value in [AudioTranscript.csproj](AudioTranscript.csproj).
2. Commit and push.
3. Run the `Publish Velopack Release` workflow again.

Installed copies will check the public release repo, download the new package automatically, and apply it on exit.

## Local Packaging Command

If you want to create release assets locally without uploading them:

```powershell
./scripts/Publish-VelopackRelease.ps1 -ReleaseRepoUrl "https://github.com/YOUR_GITHUB_NAME/AudioTranscript-Releases"
```

If you want to package and upload locally:

```powershell
./scripts/Publish-VelopackRelease.ps1 `
  -ReleaseRepoUrl "https://github.com/YOUR_GITHUB_NAME/AudioTranscript-Releases" `
  -Token "YOUR_TOKEN" `
  -PublishToGitHub
```

Local outputs are written under `artifacts/velopack`.

## Important Notes

- The Velopack application id is `JustinTagarda.AudioTranscript`. Do not change it after you ship your first real release.
- The public release repo should stay source-free. Only release assets and the default GitHub-generated archive links should be visible there.
- The current setup ships unsigned installers. Windows SmartScreen / Defender warnings are expected until you add code signing.
