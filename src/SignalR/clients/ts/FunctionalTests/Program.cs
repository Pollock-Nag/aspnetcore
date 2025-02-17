// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FunctionalTests;

public class Program
{
    public static Task Main(string[] args)
    {
        string url = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url":
                    i += 1;
                    url = args[i];
                    break;
            }
        }

        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHostBuilder =>
            {
                webHostBuilder
                .ConfigureLogging(factory =>
                {
                    factory.AddSimpleConsole(options =>
                    {
                        options.IncludeScopes = true;
                        options.TimestampFormat = "[HH:mm:ss] ";
                        options.UseUtcTimestamp = true;
                    });
                    factory.AddDebug();
                    factory.SetMinimumLevel(LogLevel.Debug);
                })
                .UseKestrel((builderContext, options) =>
                {
                    options.ConfigureHttpsDefaults(httpsOptions =>
                    {
                        bool useRSA = false;
                        if (OperatingSystem.IsWindows())
                        {
                                // Detect Win10+
                                var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                            var major = key.GetValue("CurrentMajorVersionNumber") as int?;
                            var minor = key.GetValue("CurrentMinorVersionNumber") as int?;

                            if (major.HasValue && minor.HasValue)
                            {
                                useRSA = true;
                            }
                        }
                        else
                        {
                            useRSA = true;
                        }

                        if (useRSA)
                        {
                                // RSA cert, won't work on Windows 8.1 & Windows 2012 R2 using HTTP2, and ECC won't work in some Node environments
                                var certPath = Path.Combine(Directory.GetCurrentDirectory(), "testCert.pfx");
                            httpsOptions.ServerCertificate = new X509Certificate2(certPath, "testPassword");
                        }
                        else
                        {
                                // ECC cert, works on Windows 8.1 & Windows 2012 R2 using HTTP2
                                var certPath = Path.Combine(Directory.GetCurrentDirectory(), "testCertECC.pfx");
                            httpsOptions.ServerCertificate = new X509Certificate2(certPath, "testPassword");
                        }
                    });
                })
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseIISIntegration()
                .UseStartup<Startup>();

                if (!string.IsNullOrEmpty(url))
                {
                    Console.WriteLine($"Forcing URL to: {url}");
                    webHostBuilder.UseUrls(url);
                }
            });

        return hostBuilder.Build().RunAsync();
    }
}
