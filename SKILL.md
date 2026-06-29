# ADBControl Codex Execution Skill

This file is the execution contract for every future Codex / AI-assisted change in this repository.

## 1. Mandatory workflow

Every implementation round must follow this order:

1. Read the existing context before editing.
2. Restate the locked requirements and constraints that affect the change.
3. Identify the smallest safe impact scope.
4. Write or update tests/spec cases first whenever the behavior is testable.
5. Implement the minimum necessary production code.
6. Run the narrowest relevant tests first, then broader tests when available.
7. Produce a self-check report listing changed files, tested commands, risks, and next verifications.

Do not skip directly to implementation unless the change is purely documentation.

## 2. Architecture rules

- Keep code highly modular and low-coupled.
- Do not create giant files, giant components, giant services, or catch-all helpers.
- Core protocol, device capability detection, transport, media, permission, storage, and API routing must stay in separate modules.
- Public boundaries must be defined by schemas, interfaces, or explicit adapter traits/classes.
- Platform-specific code must be isolated behind platform adapters.
- Critical logic must include concise comments explaining why the logic exists, not obvious syntax comments.

## 3. Test and spec rules

- Requirements should be recorded as executable tests/spec cases when possible.
- Tests should include short scenario comments so future AI/code agents can infer the intended behavior from the failing test.
- When behavior changes, update tests and requirements together.
- Avoid separate long-form documentation that can drift away from tests unless it summarizes stable architecture decisions.

## 4. Safety rules

- No silent failure.
- No undocumented fallback.
- No broad `catch`/`except` that hides root causes.
- No temporary hotfix that bypasses the architecture boundary.
- No direct device-control operation without going through permission and capability checks.
- No raw UDP control protocol; UDP-like transport must use a designed upper-layer protocol with authentication, session, ordering/retry strategy where needed, and replay protection.
- No direct native-plugin/system capability access without explicit manifest permission and user authorization.

## 5. ADBControl locked project direction

ADBControl is a cross-platform Android/ADB control system consisting of:

- A Core service that exposes unified HTTP APIs to callers.
- Built-in ADB capability support.
- An Android companion app channel for capabilities that are better or only possible through the app.
- A capability router that decides whether a request should use ADB, the Android app channel, or both.
- A web management UI served by Core.
- JSON Schema / OpenAPI as the authoritative control API and capability contract.

## 6. Current priority checklist

The current implementation track must make these capability areas explicit and testable:

1. Core certificate generation and server configuration.
2. Trust ingress / pairing trust boundary.
3. Core media store.
4. Android JNI native transport boundary.
5. Android pairing result connected into service state.
6. Android hello / capability / permission initial synchronization.
7. Real-time H.264 chunk streaming through `stream.open(realtime=true)` while preserving file-level MP4 recording as a separate path.

## 7. Output format for future AI rounds

Each round should end with:

- Changed files.
- Behavior added or changed.
- Tests/specs added or updated.
- Commands run and results.
- Known limitations or blocked verification.
- Next smallest safe step.
