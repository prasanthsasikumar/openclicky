// ESLint for the TypeScript workspaces and the loose `scripts/*.mjs`.
//
// Deliberately a linter and not a formatter: nothing here rewrites whitespace, quotes or commas, so
// the first run does not touch every file and bury the history of the security pass. Formatting is
// a separate decision, to be taken after the dead-code pass.
//
// Type-aware rules are on (`projectService`), which is what makes the floating-promise and
// misused-promise rules possible — those are the ones that catch real bugs in this codebase, where
// nearly everything is async and a dropped `await` means a request that silently never completes.

import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  {
    // Build output, dependencies, and the Swift side (SwiftLint's job, see .swiftlint.yml).
    ignores: [
      "**/dist/**",
      "**/node_modules/**",
      "macos/**",
      "reference/**",
      "backend/src/skills-manifest.ts",
    ],
  },

  js.configs.recommended,
  ...tseslint.configs.recommendedTypeChecked,

  {
    languageOptions: {
      parserOptions: {
        // The `typecheck` tsconfigs, not the build ones: the build configs include only `src`, so
        // every test file would come back "not found by the project service" — the same gap that
        // let an unchecked test import a type `jose` no longer exports.
        project: ["./backend/tsconfig.typecheck.json", "./agent/tsconfig.typecheck.json"],
        tsconfigRootDir: import.meta.dirname,
      },
      globals: { ...globals.node },
    },
    rules: {
      // An unawaited promise in a request handler is a response that never arrives, and this
      // codebase is almost entirely async. These three are the reason the linter is here.
      "@typescript-eslint/no-floating-promises": "error",
      "@typescript-eslint/no-misused-promises": "error",
      "require-atomic-updates": "error",

      // `_`-prefixed arguments are the established way to say "required by the signature, unused".
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_", caughtErrorsIgnorePattern: "^_" },
      ],

      // Warnings, not errors: each one is a place where a type was widened to get moving, worth
      // seeing in the list without failing the build over it while the backlog is worked down.
      "@typescript-eslint/no-explicit-any": "warn",
      "@typescript-eslint/no-unsafe-assignment": "warn",
      "@typescript-eslint/no-unsafe-member-access": "warn",
      "@typescript-eslint/no-unsafe-call": "warn",
      "@typescript-eslint/no-unsafe-argument": "warn",
      "@typescript-eslint/no-unsafe-return": "warn",
      "@typescript-eslint/restrict-template-expressions": "warn",
      "@typescript-eslint/require-await": "warn",
    },
  },

  {
    // Test files reach into internals, stub things the types do not describe, and build debug
    // strings out of whatever a fake fetch was handed. None of that is worth a rule.
    files: ["**/test/**/*.ts", "**/*.test.ts"],
    rules: {
      "@typescript-eslint/no-explicit-any": "off",
      "@typescript-eslint/no-unsafe-assignment": "off",
      "@typescript-eslint/no-unsafe-member-access": "off",
      "@typescript-eslint/no-unsafe-call": "off",
      "@typescript-eslint/no-unsafe-argument": "off",
      "@typescript-eslint/no-unsafe-return": "off",
      "@typescript-eslint/no-base-to-string": "off",
      "@typescript-eslint/restrict-plus-operands": "off",
      "@typescript-eslint/unbound-method": "off",
    },
  },

  {
    // `scripts/` is plain ESM run by node directly — no tsconfig covers it, so no type-aware rules.
    files: ["**/*.mjs"],
    extends: [tseslint.configs.disableTypeChecked],
    languageOptions: { globals: { ...globals.node } },
  },
);
