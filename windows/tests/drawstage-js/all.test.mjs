// `node --test windows/tests/drawstage-js/` resolves this directory as a module and runs its `main` (package.json),
// so the drawing stage's Node tests are all pulled in here. Each file also runs on its own:
//   node --test windows/tests/drawstage-js/anatomy-load.test.mjs
import "./anatomy-index.test.mjs";
import "./anatomy-load.test.mjs";
