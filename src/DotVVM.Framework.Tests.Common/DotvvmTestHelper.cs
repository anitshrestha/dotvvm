using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using DotVVM.Framework.Configuration;
using DotVVM.Framework.Hosting;
using DotVVM.Framework.Security;
using DotVVM.Framework.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotVVM.Framework.Tests
{
    public static class DotvvmTestHelper
    {
        class FakeProtector : IViewModelProtector
        {
            // I hope I will not see this message anywhere on the web ;)
            public const string WarningPrefix = "WARNING - Message not encryped: ";
            public static readonly byte[] WarningPrefixBytes = Convert.FromBase64String("WARNING/NOT/ENCRYPTED+++");

            public string Protect(string serializedData, IDotvvmRequestContext context)
            {
                return WarningPrefix + ": " + serializedData;
            }

            public byte[] Protect(byte[] plaintextData, params string[] purposes)
            {
                var result = new List<byte>();
                result.AddRange(WarningPrefixBytes);
                result.AddRange(plaintextData);
                return result.ToArray();
            }

            public string Unprotect(string protectedData, IDotvvmRequestContext context)
            {
                if (!protectedData.StartsWith(WarningPrefix + ": ")) throw new SecurityException($"");
                return protectedData.Remove(0, WarningPrefix.Length + 2);
            }

            public byte[] Unprotect(byte[] protectedData, params string[] purposes)
            {
                if (!protectedData.Take(WarningPrefixBytes.Length).SequenceEqual(WarningPrefixBytes)) throw new SecurityException($"");
                return protectedData.Skip(WarningPrefixBytes.Length).ToArray();
            }
        }

        public static void RegisterMoqServices(IServiceCollection services)
        {
            services.TryAddSingleton<IViewModelProtector, FakeProtector>();
        }

        public static DotvvmConfiguration CreateConfiguration(Action<IServiceCollection> customServices = null) =>
            DotvvmConfiguration.CreateDefault(s => {
                customServices?.Invoke(s);
                RegisterMoqServices(s);
            });

        public static TestDotvvmRequestContext CreateContext(DotvvmConfiguration configuration)
        {
            IServiceProvider services = configuration.ServiceProvider.CreateScope().ServiceProvider;
            var context = new TestDotvvmRequestContext()
            {
                Configuration = configuration,
                Services = services,
                CsrfToken = "Test CSRF Token",
                ModelState = new ModelState(),
                ResourceManager = services.GetService<ResourceManagement.ResourceManager>()
            };
            return context;
        }
    }
}