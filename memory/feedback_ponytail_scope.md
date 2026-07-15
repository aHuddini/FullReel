---
name: feedback_ponytail_scope
description: When to apply the Ponytail skill and the mandatory validation rule for its proposals
metadata: 
  node_type: memory
  type: feedback
  originSessionId: f9b26b4d-0157-4132-a127-abf6f578e0c9
---

Use the Ponytail skill ONLY for refactor plans/purposes, or when proposing an elegant / more effective solution for a task, function, or bugfix. Do NOT apply the laziness lens to default feature work, doc updates, or mechanical edits — build those as asked.

Any fix or change proposed via Ponytail MUST be validated that it actually works before claiming done — build + package, and where there is runtime surface, drive the affected flow (see [[feedback_dev_workflow]] and the `verify` skill), not just "it compiles".

**Why:** User wants Ponytail as a targeted refactor/optimization tool, not a global constraint on every response — and does not trust a "lazier" solution until it's proven end-to-end.

**How to apply:** Reach for Ponytail when the work is a refactor or when a cleaner/smaller alternative genuinely exists; otherwise proceed normally. Whenever Ponytail shapes the solution, verify it works (build/package/drive the flow) before reporting success.
