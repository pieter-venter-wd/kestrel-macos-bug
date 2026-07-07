# Connections to a secondary loopback alias are broken on macOS 26 + .NET 10 (apphost launch only)

## Summary

On macOS 26.5.2 (Darwin 25.5.0), when a Kestrel app is launched via the **apphost**
(`dotnet run`, a JetBrains Rider "Run" configuration, or executing the built apphost binary
directly — i.e. *not* via `dotnet <path-to-dll>` through the shared framework host), incoming
connections that target a **secondary loopback alias** (an address added to `lo0` via e.g.
`sudo ifconfig lo0 alias 10.100.100.101`) are broken. Genuine `127.0.0.1` / `::1` always work fine.

The exact symptom depends on how the Kestrel listener is bound:

| Kestrel binding | `127.0.0.1` | Secondary loopback alias (e.g. `10.100.100.101`) |
|---|---|---|
| `options.ListenAnyIP(5000)` (dual-stack `IPv6Any` + `DualMode`) | OK | **Process crashes** on accept |
| `options.Listen(IPAddress.Any, 5000)` (IPv4-only) | OK | **Connection silently dropped**, no crash |

Neither symptom reproduces via a raw `dotnet <path-to-dll>` exec (through the shared framework
host) — only via the apphost. Neither reproduces on .NET 8, on the same machine and launch method.

## Symptom 1: crash (dual-stack binding)

With `options.ListenAnyIP(5000)`, accepting a connection from the alias address throws an
unhandled `ArgumentException` on a ThreadPool worker thread inside the socket accept completion
callback, which terminates the process (`AppDomain.UnhandledException` — it is not caught by
Kestrel's own `SocketConnectionListener.AcceptAsync`, which only catches `SocketException`):

```
System.ArgumentException: The supplied System.Net.SocketAddress is an invalid size for the
System.Net.IPEndPoint end point. (Parameter 'socketAddress')
   at System.Net.IPEndPoint.Create(SocketAddress socketAddress)
   at System.Net.Sockets.SocketAsyncEventArgs.FinishOperationSyncSuccess(Int32 bytesTransferred, SocketFlags flags)
   at System.Net.Sockets.SocketAsyncEventArgs.FinishOperationAsyncSuccess(Int32 bytesTransferred, SocketFlags flags)
   at System.Net.Sockets.SocketAsyncEventArgs.AcceptCompletionCallback(IntPtr acceptedFileDescriptor, Memory`1 socketAddress, SocketError socketError)
   at System.Net.Sockets.SocketAsyncEngine.System.Threading.IThreadPoolWorkItem.Execute()
   at System.Threading.ThreadPoolWorkQueue.Dispatch()
   at System.Threading.PortableThreadPool.WorkerThread.WorkerThreadStart()
   at System.Threading.Thread.StartCallback()
```

## Symptom 2: silent connection drop (IPv4-only binding)

With `options.Listen(IPAddress.Any, 5000)` instead, the process does **not** crash — it keeps
running and continues serving `127.0.0.1` normally — but a request to the alias address gets a
full TCP handshake, the HTTP request is sent and received, and then the connection is closed
immediately with zero response bytes:

```
$ curl -v http://10.100.100.101:5000/weatherforecast
*   Trying 10.100.100.101:5000...
* Connected to 10.100.100.101 (10.100.100.101) port 5000
> GET /weatherforecast HTTP/1.1
> Host: 10.100.100.101:5000
...
* Request completely sent off
* Empty reply from server
* Closing connection
curl: (52) Empty reply from server
```

This happens near-instantly (tens of milliseconds — not a timeout), and nothing is logged by
Kestrel even with `Microsoft.AspNetCore.Server.Kestrel` set to `Debug`/`Trace`. It appears the
connection is dying somewhere below the point Kestrel's own connection-level logging would catch
it — likely inside `SocketTransportOptions.CreateDefaultBoundListenSocket` / the accepted-socket
setup path, similar to symptom 1 but not throwing far enough to become unhandled.

## Environment

- macOS 26.5.2 (Darwin 25.5.0), Apple Silicon (arm64)
- .NET SDK 10.0.301 / runtime 10.0.9
- Reproduced with both the .NET 10 SDK's default project template (`Microsoft.AspNetCore.OpenApi`)
  and a stripped-down Kestrel-only app with zero extra dependencies — not specific to any package.

## Reproduction matrix (launch method × target address)

| Launch method | `127.0.0.1` | Secondary loopback alias |
|---|---|---|
| `dotnet <built-dll>` (shared framework host) | OK | OK |
| `dotnet run` / apphost / Rider "Run" | OK | Broken (crash or silent drop, see above) |

## Steps to reproduce

1. Add a secondary loopback alias:
   ```
   sudo ifconfig lo0 alias 10.100.100.101
   ```
2. Clone/open this repo (`KestrelIssue.sln`). `Program.cs` calls
   `builder.WebHost.ConfigureKestrel(...)` — toggle between `options.ListenAnyIP(5000)` (crash) and
   `options.Listen(IPAddress.Any, 5000)` (silent drop) to see each symptom.
3. Run via the apphost path (either is equivalent):
   ```
   cd KestrelIssue
   dotnet run
   ```
   or run the built apphost binary directly:
   ```
   ./bin/Debug/net10.0/KestrelIssue
   ```
4. In another terminal:
   ```
   curl -v http://10.100.100.101:5000/weatherforecast
   ```
5. Observe the crash (dual-stack binding) or empty reply (IPv4-only binding).

To confirm the non-repro cases:
- Repeat step 4 against `http://127.0.0.1:5000/weatherforecast` instead — works fine, either binding.
- Instead of step 3, run `dotnet bin/Debug/net10.0/KestrelIssue.dll` directly (through the shared
  `dotnet` host, not the apphost) and repeat step 4 against the alias address — works fine, either
  binding.
- Switch the project's `TargetFramework` to `net8.0` and repeat steps 3–4 — works fine, either
  binding.

## Notes

- The crash first appeared after a macOS OS update; the silent-drop variant was found while trying
  to work around the crash by switching to an IPv4-only Kestrel binding.
- Binding IPv4-only does **not** fix connectivity to the alias — it only changes the crash into a
  silent failure. The only reliable workaround found so far is targeting `net8.0` instead of
  `net10.0`.
