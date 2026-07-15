---
name: feedback_readme_terse
description: "README \"What's New\" must be terse — short bullets, not paragraphs"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: f9b26b4d-0157-4132-a127-abf6f578e0c9
---

README "What's New" entries must be SHORT — one tight sentence per bullet, not a paragraph. The user has flagged over-verbose README changes more than once ("way too verbose", "you make this mistake too often").

**Why:** README is user-facing marketing/summary, not documentation. Detail belongs in CHANGELOG (developer-facing) and installer.yaml (concise `[Category]` items). Cramming root causes, mechanisms, or "worst when X" caveats into README makes it a wall of text users won't read.

**How to apply:** Each README bullet = what the user gets, in one line. Cut: implementation detail, edge-case caveats, multi-clause explanations, class/file names. If tempted to write two sentences, the second one probably belongs in CHANGELOG. When in doubt, write it tight first — easier to expand than to trim after a complaint. See [[feedback_dev_workflow]].
