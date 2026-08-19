# Security policy

Please do not open a public issue for a vulnerability that could expose traffic, bypass a routing
decision, leak credentials, or leave Windows System Proxy in an unsafe state. Use GitHub's private
security advisory reporting for this repository instead.

The application deliberately fails closed when a selected eSIM route disappears. Reports that
show a request falling back from eSIM to the ordinary upstream are treated as security issues.

Do not include live credentials, full connection logs, personal paths, or signing material in a
report. A minimal reproduction with redacted host names and paths is preferred.
