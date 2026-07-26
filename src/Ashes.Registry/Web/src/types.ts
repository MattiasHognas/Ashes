export interface Dependency {
  namespace: string;
  req: string;
}

export interface PackageVersion {
  version: string;
  hash: string;
  yanked: boolean;
  dependencies: Dependency[];
  capabilities: string[];
  size: number;
  publishedAt: string;
}

export interface Package {
  namespace: string;
  description: string;
  keywords: string[];
  owners: string[];
  latest: string | null;
  versions: PackageVersion[];
}

export interface PackageSummary {
  namespace: string;
  description: string;
  latest: string | null;
  downloads: number;
  score?: number;
}

export interface BrowseResponse {
  packages: PackageSummary[];
  nextCursor: string | null;
}

export interface SearchResponse {
  results: PackageSummary[];
  nextCursor: string | null;
}

export interface ApiErrorEnvelope {
  error?: {
    code?: string;
    message?: string;
  };
}
