# Kotak manual order / lot-size fix

## What was wrong

The manual order screenshot submitted strike `2350 CE` for expiry `06-Oct-2026`. That contract does not exist for NIFTY around 22k, so the Kotak instrument resolver correctly could not find it, but the API allowed the exception to escape and Visual Studio stopped on it.

The application also still had several old defaults of NIFTY lot size `75`, while the current Kotak NIFTY lot size is `65`.

## Fixes

- Current default live/manual lot size changed from 75 to 65.
- Manual live order quantity is calculated using the actual lot size from the Kotak scrip master, not the browser value.
- Manual lot-size field is displayed read-only and auto-syncs from open Kotak positions when available.
- Strike must be a multiple of 50.
- Very small NIFTY strikes such as 2350 are blocked in Angular with a `Did you mean 23500?` message.
- If an instrument is not found, the backend returns HTTP 400 with nearby valid strikes rather than throwing an unhandled exception.
- The instrument resolver also reports the broker lot size in its validation error.

## Current example

`11 lots x 65 = 715`, matching the open Kotak positions shown in the broker screenshot.
