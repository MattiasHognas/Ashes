import type {
  ApiErrorEnvelope,
  BrowseResponse,
  Package,
  PackageSummary,
  SearchResponse,
} from "./types";

export class RegistryApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
  ) {
    super(message);
  }
}

async function request<T>(path: string): Promise<T> {
  const response = await fetch(path, {
    headers: { Accept: "application/json" },
  });
  if (!response.ok) {
    let message = `The registry returned ${response.status}.`;
    try {
      const body = (await response.json()) as ApiErrorEnvelope;
      message = body.error?.message ?? message;
    } catch {
      // Keep the HTTP fallback when an intermediary returns non-JSON.
    }

    throw new RegistryApiError(message, response.status);
  }

  return (await response.json()) as T;
}

function queryString(values: Record<string, string | number | undefined>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) {
    if (value !== undefined && value !== "") {
      params.set(name, String(value));
    }
  }

  const query = params.toString();
  return query.length > 0 ? `?${query}` : "";
}

export async function browsePackages(
  sort = "recent",
  limit = 20,
  cursor?: string,
): Promise<{ packages: PackageSummary[]; nextCursor: string | null }> {
  return request<BrowseResponse>(
    `/api/v1/packages${queryString({ sort, limit, cursor })}`,
  );
}

export async function searchPackages(
  query: string,
  limit = 20,
  cursor?: string,
): Promise<{ packages: PackageSummary[]; nextCursor: string | null }> {
  const response = await request<SearchResponse>(
    `/api/v1/search${queryString({ q: query, limit, cursor })}`,
  );
  return { packages: response.results, nextCursor: response.nextCursor };
}

export function getPackage(namespace: string): Promise<Package> {
  return request<Package>(`/api/v1/packages/${encodeURIComponent(namespace)}`);
}

export async function getReadme(namespace: string, version: string): Promise<string | null> {
  const response = await fetch(
    `/api/v1/packages/${encodeURIComponent(namespace)}/${encodeURIComponent(version)}/readme`,
    { headers: { Accept: "text/markdown" } },
  );
  if (response.status === 204) {
    return null;
  }

  if (!response.ok) {
    throw new RegistryApiError("The README could not be loaded.", response.status);
  }

  return response.text();
}
