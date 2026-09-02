import { describe, it, expect } from "vitest";
import path from "node:path";
import { resolveConfig, repoRoot } from "../src/config.js";

describe("resolveConfig", () => {
  it("applies defaults", () => {
    const c = resolveConfig({}, { HOME: "/home/u" } as NodeJS.ProcessEnv);
    expect(c.backendUrl).toBe("http://localhost:8787");
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
    expect(path.basename(repoRoot())).toBe("openclicky");
  });
});
