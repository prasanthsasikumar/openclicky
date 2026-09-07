/** Minimal PostgREST client (Supabase REST) over fetch: works on Node and Workers, no SDK. */
export class SupabaseRest {
  private readonly base: string;

  constructor(
    baseUrl: string,
    private readonly serviceKey: string,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {
    this.base = baseUrl.replace(/\/+$/, "") + "/rest/v1";
  }

  private headers(extra: Record<string, string> = {}): Record<string, string> {
    return { apikey: this.serviceKey, authorization: `Bearer ${this.serviceKey}`, "content-type": "application/json", ...extra };
  }

  private async check(res: Response, what: string): Promise<void> {
    if (!res.ok) throw new Error(`supabase ${what} failed (${res.status}): ${(await res.text()).slice(0, 300)}`);
  }

  /** `query` is a PostgREST query string, e.g. `user_id=eq.abc&select=*`. */
  async select<T>(table: string, query: string): Promise<T[]> {
    const res = await this.fetchImpl(`${this.base}/${table}?${query}`, { headers: this.headers() });
    await this.check(res, `select ${table}`);
    return (await res.json()) as T[];
  }

  async insert<T>(table: string, row: Record<string, unknown>): Promise<T> {
    const res = await this.fetchImpl(`${this.base}/${table}`, {
      method: "POST",
      headers: this.headers({ prefer: "return=representation" }),
      body: JSON.stringify(row),
    });
    await this.check(res, `insert ${table}`);
    return ((await res.json()) as T[])[0];
  }

  async upsert<T>(table: string, row: Record<string, unknown>, onConflict: string): Promise<T> {
    const res = await this.fetchImpl(`${this.base}/${table}?on_conflict=${onConflict}`, {
      method: "POST",
      headers: this.headers({ prefer: "resolution=merge-duplicates,return=representation" }),
      body: JSON.stringify(row),
    });
    await this.check(res, `upsert ${table}`);
    return ((await res.json()) as T[])[0];
  }
}
