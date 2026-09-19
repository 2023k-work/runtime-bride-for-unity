# Changelog

## [0.1.1] - 2026-09-19

- Keep queued Player operations alive when the application loses focus by
  enabling `Application.runInBackground` at bridge startup.
- Reserve the built-in `echo` and `smoke` command names so product handlers
  cannot be overwritten by bridge initialization.
- Update the BasicBridge sample to use `sample.echo`.

## [0.1.0] - 2026-09-19

- Initial Unity 6 runtime bridge package.
- Loopback JSON Lines transport with main-thread dispatch.
- Session validation and extensible command registration.
