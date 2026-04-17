# Contributing to Copilot Studio A2A

You can contribute to Copilot Studio A2A with issues and pull requests (PRs). Simply
filing issues for problems you encounter is a great way to contribute. Contributing
code is greatly appreciated.

## Reporting Issues

We always welcome bug reports, API proposals, and overall feedback. Here are a few
tips on how you can make reporting your issue as effective as possible.

### Where to Report

New issues can be reported in our [list of issues](https://github.com/bradrlaw/copilot-studio-a2a/issues).

Before filing a new issue, please search the list of issues to make sure it does
not already exist.

If you do find an existing issue for what you wanted to report, please include
your own feedback in the discussion. Do consider upvoting (👍 reaction) the original
post, as this helps us prioritize popular issues in our backlog.

### Writing a Good Bug Report

Good bug reports make it easier for maintainers to verify and root cause the
underlying problem. The better a bug report, the faster the problem will be resolved.
Ideally, a bug report should contain the following information:

- A high-level description of the problem.
- A _minimal reproduction_, i.e. the smallest size of code/configuration required
  to reproduce the wrong behavior.
- A description of the _expected behavior_, contrasted with the _actual behavior_ observed.
- Information on the environment: OS, .NET SDK version, Copilot Studio region, etc.
- Additional information, e.g. Is it a regression from previous versions? Are there
  any known workarounds?

## Contributing Changes

Project maintainers will merge accepted code changes from contributors.

### DOs and DON'Ts

DO's:

- **DO** follow the standard [.NET coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions).
- **DO** give priority to the current style of the project or file you're changing
  if it diverges from the general guidelines.
- **DO** include tests when adding new features. When fixing bugs, start with
  adding a test that highlights how the current behavior is broken.
- **DO** keep the discussions focused. When a new or related topic comes up
  it's often better to create a new issue than to side-track the discussion.
- **DO** clearly state on an issue that you are going to take on implementing it.

DON'Ts:

- **DON'T** surprise us with big pull requests. Instead, file an issue and start
  a discussion so we can agree on a direction before you invest a large amount of time.
- **DON'T** commit code that you didn't write. If you find code that you think is a good
  fit to add to this project, file an issue and start a discussion before proceeding.
- **DON'T** submit PRs that alter licensing related files or headers. If you believe
  there's a problem with them, file an issue and we'll be happy to discuss it.

### Suggested Workflow

We use and recommend the following workflow:

1. Create an issue for your work.
   - You can skip this step for trivial changes.
   - Reuse an existing issue on the topic, if there is one.
   - Get agreement from the team and the community that your proposed change is
     a good one.
2. Create a personal fork of the repository on GitHub (if you don't already have one).
3. In your fork, create a branch off of main (`git checkout -b mybranch`).
   - Name the branch so that it clearly communicates your intentions, such as
     "issue-123" or "githubhandle-issue".
4. Make and commit your changes to your branch.
5. Build the project and run existing tests to ensure nothing is broken:
   ```bash
   dotnet build
   dotnet test  # when tests are available
   ```
6. Create a PR against the repository's **main** branch.
   - State in the description what issue or improvement your change is addressing.
   - Verify that all Continuous Integration checks are passing.
7. Wait for feedback or approval of your changes from the code maintainers.
8. When maintainers have signed off, and all checks are green, your PR will be merged.

### Contributor License Agreement

This project welcomes contributions and suggestions. Most contributions require you to
agree to a Contributor License Agreement (CLA) declaring that you have the right to, and
actually do, grant us the rights to use your contribution. For details, visit
https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need
to provide a CLA and decorate the PR appropriately (e.g., status check, comment). Simply
follow the instructions provided by the bot. You will only need to do this once across all
repos using our CLA.
