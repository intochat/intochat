// The command-line host. It talks to the kernel's edge only, never to the Orleans cluster: the gRPC neuron channel (gRPC-Web, so
// plain http on this machine works as well as https to a remote install) and, to answer a confirmation, the HTTP edge.
//   runs                          list the runs index
//   send <app> <command> [json]   send a command to an installed sandbox app (one app run)
//   start <workflow> [input]      start a workflow run
//   cancel <run id> [reason]      stop a waiting workflow or code block run
//   approve|deny <plan id>        answer a confirmation as the person at this machine
// Options: --base <url> (default http://localhost:5080) --brain <id> (default owner) --limit <n> (default 20); each also reads
// INTOCHAT_<NAME>. The proof of who is asking comes from the environment, never the command line: INTOCHAT_PRESENCE (the local
// presence token), INTOCHAT_BASIC (user:password) or INTOCHAT_SESSION (a session cookie, name=value). runs, send, start and cancel
// act as the app "cli" on behalf of that person; approve and deny are the person's own act, so they do not travel as the app.
return await IntoChat.OS.Cli.CliCommands.Run(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable);
