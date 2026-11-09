# Live Currency & Crypto Monitor

Monitors live exchange rates (EUR/USD) every 5 minutes and flags threshold alerts when volatility spikes.

- Every `5m`, polls rates feed through `rates` slot.
- Updates `EurUsd` and triggers alert when rate drops below 1.05.
- Emits `fx.polled(double EurUsd)`.
- Reacts to `fx.reset` to clear active alert.
- The view shows tabular rates and an alert banner.
