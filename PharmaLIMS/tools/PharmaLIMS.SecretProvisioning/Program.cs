using System;
using System.Text;
using PharmaLIMS.Infrastructure;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("PharmaLIMS Secret Provisioning must run on the Windows account that will run PharmaLIMS.");
            return 2;
        }

        Console.WriteLine("PharmaLIMS SQL Authentication DPAPI Provisioning");
        Console.WriteLine($"Windows account: {Environment.UserDomainName}\\{Environment.UserName}");
        Console.WriteLine("Enter the SQL password. Input is hidden and is never written to disk or logs.");
        string secret = ReadSecret();
        if (string.IsNullOrEmpty(secret))
        {
            Console.Error.WriteLine("No password was entered.");
            return 3;
        }

        try
        {
            string protectedValue = ProtectedSecretStore.ProtectCurrentUserBase64(secret);
            Console.WriteLine();
            Console.WriteLine("Copy the following value into Database:DpapiProtectedPassword on this same Windows account:");
            Console.WriteLine(protectedValue);
            Console.WriteLine();
            Console.WriteLine("Keep Database:Password empty. Re-provision after changing the Windows run account or SQL password.");
            return 0;
        }
        finally
        {
            secret = string.Empty;
        }
    }

    private static string ReadSecret()
    {
        var value = new StringBuilder();
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
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
