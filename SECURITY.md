# Security Policy

## Supported versions

Security fixes are provided for the latest released version of HelloLock.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting feature:

1. Open the repository's **Security** tab.
2. Select **Advisories**.
3. Select **Report a vulnerability**.

Do not include credential material, PINs, authentication buffers, tokens, or
other private data in a report. A minimal reproduction and affected Windows
version are sufficient.

## Security scope

HelloLock is an application-level interaction guard, not a Windows security
boundary. Reports that demonstrate one of the following are in scope:

- unlocking without successful credential verification;
- accepting credentials for a different Windows user;
- exposing or persisting serialized credential data;
- escaping the overlay through ordinary, non-administrative desktop input;
- installation or update behavior that executes untrusted content.

Administrative process termination, SYSTEM-level control, remote management,
debugging or injection with equivalent privileges, forced sign-out, reboot, and
application crashes are known limitations of the threat model.

Ordinary mouse input is blocked by a low-level mouse hook. Touch and pen input
that Windows does not promote to mouse messages, plus system UI placed in a
higher window band, remain outside the guaranteed pointer-blocking boundary.
Windows credential UI remains interactive through the operating system's input
isolation rather than an application-defined pointer pass-through region.
