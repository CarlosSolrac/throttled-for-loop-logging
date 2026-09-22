# Code signing policy

This is the code signing policy that the [SignPath Foundation](https://signpath.org/terms.html)
asks open source projects to publish.

> **Status.** An application to the SignPath Foundation is pending, so releases published so far are
> unsigned. Everything below describes how signing works once the subscription is active.

## Attribution

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## Team roles

| Role | Members | What they do |
| --- | --- | --- |
| Committers and reviewers | [@CarlosSolrac](https://github.com/CarlosSolrac) | Write the code, review every pull request into `main`. |
| Approvers | [@CarlosSolrac](https://github.com/CarlosSolrac) | Approve each signing request in SignPath before a release is signed. |

Anyone with write access to this repository is required to have two-factor authentication enabled on
GitHub and on SignPath.

## Privacy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.

The library only writes log records to the `ILogger` that the host application supplies. It opens no
network connections, reads no environment or machine state, and starts no processes.

## What is signed, and how

- The signed artifact is the NuGet package `ThrottledForLoopLogging`, together with the
  `ThrottledLogging.dll` assemblies it carries for `net8.0` and `net10.0`.
- Binaries are built only by GitHub Actions on GitHub-hosted runners, from a tagged commit in this
  repository, by the release workflow. No binary is built or uploaded from a developer machine.
- Product name and product version metadata are set in `Directory.Build.props` and are the same in
  every build of a given version; the SignPath artifact configuration enforces them.
- Every signing request needs manual approval from an approver listed above.

[`docs/code-signing.md`](docs/code-signing.md) describes the build-side wiring.
