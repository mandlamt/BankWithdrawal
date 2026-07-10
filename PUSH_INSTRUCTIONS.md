# Pushing to GitHub — what I did and what you need to do

I prepared the project locally in this sandbox, but **I cannot push to your
GitHub account for you**: I do not have your credentials, and pushing code
on your behalf is an action that must be authenticated as you.

Here is the state of the world and the exact steps to finish.

## What I verified

| Check                                       | Result                                                                 |
|---------------------------------------------|------------------------------------------------------------------------|
| GitHub user `mandlamt`                      | Exists (account id 8694526)                                            |
| Repo `https://github.com/mandlamt/BankWithdrawal` | **Does not exist yet** (HTTP 404 from the GitHub API)              |
| Local git repo                              | Initialized, `.gitignore` added, initial commit made on `main`         |
| Remote `origin`                             | Set to `https://github.com/mandlamt/BankWithdrawal.git` (not yet pushed) |

## What I did to the code

1. Extracted `BankWithdrawal.zip` from the user input files.
2. Added a standard .NET / Visual Studio `.gitignore` (excludes `bin/`,
   `obj/`, `.vs/`, `*.user`, `*.suo`, `packages/`, etc.).
3. `git init -b main`, then a single commit:
   *"Initial commit: BankWithdrawal C#/ASP.NET Core 8 redesign"*
   — 19 files, 1073 insertions.
4. Added the remote `origin` pointing at
   `https://github.com/mandlamt/BankWithdrawal.git`.

## What you need to do (two steps)

### Step 1 — Create the empty repo on GitHub

Go to **https://github.com/new** and fill in:

- **Owner:** `mandlamt`
- **Repository name:** `BankWithdrawal`
- **Description:** *C# / ASP.NET Core 8 redesign of a bank withdrawal endpoint
  (transactional outbox, idempotency, atomic balance check).*
- **Public / Private:** your choice
- ⚠️ **Do NOT** check "Add a README file", "Add .gitignore", or "Choose a
  license" — we already have these in the commit. (If you do, the push
  below will be rejected as a non-fast-forward and you'll need to pull
  and merge, or just delete the repo and recreate it empty.)

Click **Create repository**.

### Step 2 — Push

You can run `push.sh` from the repo root, or run the equivalent git
commands directly:

```bash
cd /path/to/BankWithdrawal

# (only if origin is not already set)
git remote add origin https://github.com/mandlamt/BankWithdrawal.git

git push -u origin main
```

When prompted:

- **HTTPS + PAT:** paste a GitHub Personal Access Token (Settings →
  Developer settings → Personal access tokens → Tokens (classic), with
  `repo` scope). The token is the password.
- **HTTPS + GitHub CLI:** run `gh auth login` once, then `gh repo create
  mandlamt/BankWithdrawal --source=. --remote=origin --push` (only if
  the repo was created via the CLI; otherwise just `git push -u origin
  main` will work after `gh auth login`).
- **SSH:** make sure `git@github.com:mandlamt/BankWithdrawal.git` is the
  remote (not the HTTPS one). `git remote set-url origin
  git@github.com:mandlamt/BankWithdrawal.git`, then `git push -u origin
  main`.

That's it — the same `dfeb475` commit I made locally will be the first
commit on `main`.
