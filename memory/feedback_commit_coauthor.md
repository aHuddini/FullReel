---
name: commit-coauthor-format
description: Use minimal Co-Authored-By without model name in git commits
metadata: 
  node_type: memory
  type: feedback
  originSessionId: f9b26b4d-0157-4132-a127-abf6f578e0c9
---

Use `Co-Authored-By: Claude` in commits — no model name, no email address.

**Why:** User wants minimal, clean co-author attribution without revealing model details or fake emails.

**How to apply:** Every git commit that includes a Co-Authored-By line — including commits made by dispatched subagents (instruct them to use exactly `Co-Authored-By: Claude`, never the model id like "Claude Opus 4.8" and no email). The co-author line itself is WANTED (project convention); the ONLY thing to avoid is the model name / email. Do not strip the co-author entirely — keep it minimal.
