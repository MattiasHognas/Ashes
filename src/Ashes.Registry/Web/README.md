# Ashes Registry Web

The registry UI is a Vue 3 and TypeScript SPA built with Vite. Production assets are served by the
same ASP.NET Core host as `/api/v1`.

## Development

Build and run the complete registry:

```sh
dotnet run --project src/Ashes.Registry
```

The .NET build restores the locked frontend dependencies and runs the Vite production build
automatically. To use Vite's development server and hot module replacement, run the API and frontend
in separate terminals:

```sh
dotnet run --project src/Ashes.Registry -p:BuildRegistryWeb=false -- --urls http://localhost:5000
cd src/Ashes.Registry/Web
pnpm install --frozen-lockfile
pnpm dev
```

Set `ASHES_REGISTRY_PROXY` before `pnpm dev` when the API listens at a different origin.

Run frontend-only checks with:

```sh
cd src/Ashes.Registry/Web
pnpm run build
```
