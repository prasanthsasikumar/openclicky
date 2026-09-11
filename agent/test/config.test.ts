import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { resolveConfig, repoRoot } from "../src/config.js";

describe("resolveConfig", () => {
  it("applies defaults", () => {
    const c = resolveConfig({}, { HOME: "/home/u" } as NodeJS.ProcessEnv);
    expect(c.backendUrl).toBe("https://api.openclicky.flowsxr.com");
    expect(c.codexHome).toBe("/home/u/.openclicky/codex-home");
    expect(c.codexBin).toBe("codex");
    expect(c.workspace).toBe(process.cwd());
    expect(c.token).toBeUndefined();
    expect(c.model).toBeUndefined();
    expect(c.verbose).toBe(false);
  });
  it("env then flags win", () => {
    const c = resolveConfig(
      { token: "flag" },
      { HOME: "/h", BACKEND_URL: "http://b:1/", OPENCLICKY_TOKEN: "env", OPENCLICKY_WORKSPACE: "/ws", OPENCLICKY_MODEL: "m" } as NodeJS.ProcessEnv,
    );
    expect(c.backendUrl).toBe("http://b:1");
    expect(c.token).toBe("flag");
    expect(c.workspace).toBe("/ws");
    expect(c.model).toBe("m");
  });
  it("repoRoot points at the directory holding skills/ and config/", () => {
    const root = repoRoot();
    expect(fs.existsSync(path.join(root, "skills", "ModelInstructions.md"))).toBe(true);
    expect(fs.existsSync(path.join(root, "config", "codex-config.toml"))).toBe(true);
  });

  describe("runTimeoutMs", () => {
    it("defaults to 10 minutes", () => {
      expect(resolveConfig({}, { HOME: "/home/u" } as NodeJS.ProcessEnv).runTimeoutMs).toBe(10 * 60 * 1000);
    });
    it("takes an override from the env", () => {
      expect(resolveConfig({}, { HOME: "/home/u", OPENCLICKY_RUN_TIMEOUT_MS: "5000" } as NodeJS.ProcessEnv).runTimeoutMs).toBe(5000);
    });
    it("ignores a non-positive env value and falls back to the default", () => {
      expect(resolveConfig({}, { HOME: "/home/u", OPENCLICKY_RUN_TIMEOUT_MS: "not-a-number" } as NodeJS.ProcessEnv).runTimeoutMs).toBe(10 * 60 * 1000);
      expect(resolveConfig({}, { HOME: "/home/u", OPENCLICKY_RUN_TIMEOUT_MS: "0" } as NodeJS.ProcessEnv).runTimeoutMs).toBe(10 * 60 * 1000);
    });
    it("lets a flag win over the env", () => {
      expect(resolveConfig({ runTimeoutMs: 42 }, { HOME: "/home/u", OPENCLICKY_RUN_TIMEOUT_MS: "5000" } as NodeJS.ProcessEnv).runTimeoutMs).toBe(42);
    });
  });
});
