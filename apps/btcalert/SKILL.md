---
name: btcalert
title: BTC Alert
description: Polls Coinbase BTC/USD spot every minute and sends a one-shot notification when price drops below the configured threshold.
capabilities:
  - http@1 (api.coinbase.com)
  - notifications@1
schedule: every 1m
---

# BTC Alert (`btcalert`)

Watches Bitcoin spot price (`https://api.coinbase.com/v2/prices/BTC-USD/spot`) every minute using deferred `Asks<IHttpRead>` and dispatches a high-priority brain notification via `Asks<INotifications>` when spot drops below `Threshold`.

## Domain Rules & Reactions
- **Positive Threshold (`btcalert.positive-threshold`):** Threshold updates (`btcalert.setthreshold`) must be between `$0.01` and `$10,000,000`; invalid values emit `rule.rejected` without mutating state.
- **Every 1m (`Poll`):** Emits a repeatable `market.GetJson` ask keyed `"spot"`.
- **On `Answered` (`Heard`):** Updates `LastPrice`. When `Armed && price < Threshold`, flips `Armed = false`, increments `AlertsSent`, journals `btcalert.pricedropped`, and asks `notify` to send one alert (`drop-{AlertsSent}`). Subsequent ticks while disarmed update `LastPrice` without spamming duplicate notifications.
- **Re-arm (`btcalert.rearm`):** Re-arms the alert (`Armed = true`).

## Ino Commands
- Publish `btcalert.setthreshold` with `{"value": 90000}` to update the USD alert floor and arm monitoring.
- Publish `btcalert.rearm` with `{}` to re-arm after a drop alert has fired.
