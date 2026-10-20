using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Hosting;
using Servitium.Infrastructure.Security;

namespace Servitium.Agent.Security;

internal static class RunCredentialCommand
{
    internal static async Task<int?> TryExecuteAsync(string[] args, AgentHostOptions options, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !string.Equals(args[0], "credentials", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using RunCredentialStore store = new(options, new DpapiSecretProtector());
        if (args.Length == 2 && string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<RunCredentialSummary> items = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (RunCredentialSummary item in items)
            {
                await Console.Out.WriteLineAsync($"{item.Alias}\t{FormatAccount(item)}\t{item.UpdatedAt:O}").ConfigureAwait(false);
            }

            return 0;
        }

        if (args.Length == 3 && string.Equals(args[1], "delete", StringComparison.OrdinalIgnoreCase))
        {
            await store.DeleteAsync(args[2], cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (args.Length is 4 or 5 && string.Equals(args[1], "add", StringComparison.OrdinalIgnoreCase))
        {
            string password = ReadSecret("Password: ");
            string confirmation = ReadSecret("Confirm password: ");
            try
            {
                if (!string.Equals(password, confirmation, StringComparison.Ordinal))
                {
                    await Console.Error.WriteLineAsync(FormattableString.Invariant($"Passwords do not match.{string.Empty}")).ConfigureAwait(false);
                    return 2;
                }

                await store.AddOrReplaceAsync(args[2], args[3], args.Length == 5 ? args[4] : null, password, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                password = string.Empty;
                confirmation = string.Empty;
            }
        }

        await Console.Error.WriteLineAsync(FormattableString.Invariant($"Usage: Servitium.Agent credentials list | add <alias> <user> [domain] | delete <alias>{string.Empty}")).ConfigureAwait(false);
        return 2;
    }

    private static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Credential passwords must be entered from an interactive console.");
        }

        Console.Write(prompt);
        StringBuilder value = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    _ = value.Remove(value.Length - 1, 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                _ = value.Append(key.KeyChar);
            }
        }
    }

    private static string FormatAccount(RunCredentialSummary item)
    {
        return string.IsNullOrWhiteSpace(item.Domain) ? item.UserName : item.Domain + "\\" + item.UserName;
    }
}
