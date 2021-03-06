// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Authentication;
using Microsoft.AspNetCore.Http.Features.Authentication;
using Microsoft.AspNetCore.Identity.Test;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.AspNetCore.Identity.InMemory
{
    public class FunctionalTest
    {
        const string TestPassword = "1qaz!QAZ";

        [Fact]
        public void UseIdentityThrowsWithoutAddIdentity()
        {
            var builder = new WebHostBuilder()
                .Configure(app => app.UseIdentity());
            Assert.Throws<InvalidOperationException>(() => new TestServer(builder));
        }

        [Fact]
        public async Task CanChangePasswordOptions()
        {
            var clock = new TestClock();
            var server = CreateServer(services => services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireDigit = false;
            }));

            var transaction1 = await SendAsync(server, "http://example.com/createSimple");

            // Assert
            Assert.Equal(HttpStatusCode.OK, transaction1.Response.StatusCode);
            Assert.Null(transaction1.SetCookie);
        }

        [Fact]
        public async Task CanCreateMeLoginAndCookieStopsWorkingAfterExpiration()
        {
            var clock = new TestClock();
            var server = CreateServer(services => services.Configure<IdentityOptions>(options =>
            {
                options.Cookies.ApplicationCookie.SystemClock = clock;
                options.Cookies.ApplicationCookie.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                options.Cookies.ApplicationCookie.SlidingExpiration = false;
            }));

            var transaction1 = await SendAsync(server, "http://example.com/createMe");
            Assert.Equal(HttpStatusCode.OK, transaction1.Response.StatusCode);
            Assert.Null(transaction1.SetCookie);

            var transaction2 = await SendAsync(server, "http://example.com/pwdLogin/false");
            Assert.Equal(HttpStatusCode.OK, transaction2.Response.StatusCode);
            Assert.NotNull(transaction2.SetCookie);
            Assert.DoesNotContain("; expires=", transaction2.SetCookie);

            var transaction3 = await SendAsync(server, "http://example.com/me", transaction2.CookieNameValue);
            Assert.Equal("hao", FindClaimValue(transaction3, ClaimTypes.Name));
            Assert.Null(transaction3.SetCookie);

            clock.Add(TimeSpan.FromMinutes(7));

            var transaction4 = await SendAsync(server, "http://example.com/me", transaction2.CookieNameValue);
            Assert.Equal("hao", FindClaimValue(transaction4, ClaimTypes.Name));
            Assert.Null(transaction4.SetCookie);

            clock.Add(TimeSpan.FromMinutes(7));

            var transaction5 = await SendAsync(server, "http://example.com/me", transaction2.CookieNameValue);
            Assert.Null(FindClaimValue(transaction5, ClaimTypes.Name));
            Assert.Null(transaction5.SetCookie);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CanCreateMeLoginAndSecurityStampExtendsExpiration(bool rememberMe)
        {
            var clock = new TestClock();
            var server = CreateServer(services => services.Configure<IdentityOptions>(options =>
            {
                options.Cookies.ApplicationCookie.SystemClock = clock;
            }));

            var transaction1 = await SendAsync(server, "http://example.com/createMe");
            Assert.Equal(HttpStatusCode.OK, transaction1.Response.StatusCode);
            Assert.Null(transaction1.SetCookie);

            var transaction2 = await SendAsync(server, "http://example.com/pwdLogin/" + rememberMe);
            Assert.Equal(HttpStatusCode.OK, transaction2.Response.StatusCode);
            Assert.NotNull(transaction2.SetCookie);
            if (rememberMe)
            {
                Assert.Contains("; expires=", transaction2.SetCookie);
            }
            else
            {
                Assert.DoesNotContain("; expires=", transaction2.SetCookie);
            }

            var transaction3 = await SendAsync(server, "http://example.com/me", transaction2.CookieNameValue);
            Assert.Equal("hao", FindClaimValue(transaction3, ClaimTypes.Name));
            Assert.Null(transaction3.SetCookie);

            // Make sure we don't get a new cookie yet
            clock.Add(TimeSpan.FromMinutes(10));
            var transaction4 = await SendAsync(server, "http://example.com/me", transaction2.CookieNameValue);
            Assert.Equal("hao", FindClaimValue(transaction4, ClaimTypes.Name));
            Assert.Null(transaction4.SetCookie);

            // Go past SecurityStampValidation interval and ensure we get a new cookie
            clock.Add(TimeSpan.FromMinutes(21));

            var transaction5 = await SendAsync(server, "http://example.com/me", transaction2.CookieNameValue);
            Assert.NotNull(transaction5.SetCookie);
            Assert.Equal("hao", FindClaimValue(transaction5, ClaimTypes.Name));

            // Make sure new cookie is valid
            var transaction6 = await SendAsync(server, "http://example.com/me", transaction5.CookieNameValue);
            Assert.Equal("hao", FindClaimValue(transaction6, ClaimTypes.Name));
        }

        [Fact]
        public async Task TwoFactorRememberCookieVerification()
        {
            var server = CreateServer();

            var transaction1 = await SendAsync(server, "http://example.com/createMe");
            Assert.Equal(HttpStatusCode.OK, transaction1.Response.StatusCode);
            Assert.Null(transaction1.SetCookie);

            var transaction2 = await SendAsync(server, "http://example.com/twofactorRememeber");
            Assert.Equal(HttpStatusCode.OK, transaction2.Response.StatusCode);

            var setCookie = transaction2.SetCookie;
            Assert.Contains(new IdentityCookieOptions().TwoFactorRememberMeCookieAuthenticationScheme + "=", setCookie);
            Assert.Contains("; expires=", setCookie);

            var transaction3 = await SendAsync(server, "http://example.com/isTwoFactorRememebered", transaction2.CookieNameValue);
            Assert.Equal(HttpStatusCode.OK, transaction3.Response.StatusCode);
        }

        private static string FindClaimValue(Transaction transaction, string claimType)
        {
            var claim = transaction.ResponseElement.Elements("claim").SingleOrDefault(elt => elt.Attribute("type").Value == claimType);
            if (claim == null)
            {
                return null;
            }
            return claim.Attribute("value").Value;
        }

        private static async Task<XElement> GetAuthData(TestServer server, string url, string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", cookie);

            var response2 = await server.CreateClient().SendAsync(request);
            var text = await response2.Content.ReadAsStringAsync();
            var me = XElement.Parse(text);
            return me;
        }

        private static TestServer CreateServer(Action<IServiceCollection> configureServices = null, Func<HttpContext, Task> testpath = null, Uri baseAddress = null)
        {
            var builder = new WebHostBuilder()
                .Configure(app =>
                {
                    app.UseIdentity();
                    app.Use(async (context, next) =>
                    {
                        var req = context.Request;
                        var res = context.Response;
                        var userManager = context.RequestServices.GetRequiredService<UserManager<TestUser>>();
                        var signInManager = context.RequestServices.GetRequiredService<SignInManager<TestUser>>();
                        PathString remainder;
                        if (req.Path == new PathString("/normal"))
                        {
                            res.StatusCode = 200;
                        }
                        else if (req.Path == new PathString("/createMe"))
                        {
                            var result = await userManager.CreateAsync(new TestUser("hao"), TestPassword);
                            res.StatusCode = result.Succeeded ? 200 : 500;
                        }
                        else if (req.Path == new PathString("/createSimple"))
                        {
                            var result = await userManager.CreateAsync(new TestUser("simple"), "aaaaaa");
                            res.StatusCode = result.Succeeded ? 200 : 500;
                        }
                        else if (req.Path == new PathString("/protected"))
                        {
                            res.StatusCode = 401;
                        }
                        else if (req.Path.StartsWithSegments(new PathString("/pwdLogin"), out remainder))
                        {
                            var isPersistent = bool.Parse(remainder.Value.Substring(1));
                            var result = await signInManager.PasswordSignInAsync("hao", TestPassword, isPersistent, false);
                            res.StatusCode = result.Succeeded ? 200 : 500;
                        }
                        else if (req.Path == new PathString("/twofactorRememeber"))
                        {
                            var user = await userManager.FindByNameAsync("hao");
                            await signInManager.RememberTwoFactorClientAsync(user);
                            res.StatusCode = 200;
                        }
                        else if (req.Path == new PathString("/isTwoFactorRememebered"))
                        {
                            var user = await userManager.FindByNameAsync("hao");
                            var result = await signInManager.IsTwoFactorClientRememberedAsync(user);
                            res.StatusCode = result ? 200 : 500;
                        }
                        else if (req.Path == new PathString("/twofactorSignIn"))
                        {
                        }
                        else if (req.Path == new PathString("/me"))
                        {
                            var auth = new AuthenticateContext("Application");
                            auth.Authenticated(context.User, new AuthenticationProperties().Items, new AuthenticationDescription().Items);
                            Describe(res, auth);
                        }
                        else if (req.Path.StartsWithSegments(new PathString("/me"), out remainder))
                        {
                            var auth = new AuthenticateContext(remainder.Value.Substring(1));
                            await context.Authentication.AuthenticateAsync(auth);
                            Describe(res, auth);
                        }
                        else if (req.Path == new PathString("/testpath") && testpath != null)
                        {
                            await testpath(context);
                        }
                        else
                        {
                            await next();
                        }
                    });
                })
                .ConfigureServices(services =>
                {
                    services.AddIdentity<TestUser, TestRole>();
                    services.AddSingleton<IUserStore<TestUser>, InMemoryStore<TestUser, TestRole>>();
                    services.AddSingleton<IRoleStore<TestRole>, InMemoryStore<TestUser, TestRole>>();
                    if (configureServices != null)
                    {
                        configureServices(services);
                    }
                });
            var server = new TestServer(builder);
            server.BaseAddress = baseAddress;
            return server;
        }

        private static void Describe(HttpResponse res, AuthenticateContext result)
        {
            res.StatusCode = 200;
            res.ContentType = "text/xml";
            var xml = new XElement("xml");
            if (result != null && result.Principal != null)
            {
                xml.Add(result.Principal.Claims.Select(claim => new XElement("claim", new XAttribute("type", claim.Type), new XAttribute("value", claim.Value))));
            }
            if (result != null && result.Properties != null)
            {
                xml.Add(result.Properties.Select(extra => new XElement("extra", new XAttribute("type", extra.Key), new XAttribute("value", extra.Value))));
            }
            using (var memory = new MemoryStream())
            {
                using (var writer = XmlWriter.Create(memory, new XmlWriterSettings { Encoding = Encoding.UTF8 }))
                {
                    xml.WriteTo(writer);
                }
                res.Body.Write(memory.ToArray(), 0, memory.ToArray().Length);
            }
        }

        private static async Task<Transaction> SendAsync(TestServer server, string uri, string cookieHeader = null, bool ajaxRequest = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.Add("Cookie", cookieHeader);
            }
            if (ajaxRequest)
            {
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            }
            var transaction = new Transaction
            {
                Request = request,
                Response = await server.CreateClient().SendAsync(request),
            };
            if (transaction.Response.Headers.Contains("Set-Cookie"))
            {
                transaction.SetCookie = transaction.Response.Headers.GetValues("Set-Cookie").SingleOrDefault();
            }
            if (!string.IsNullOrEmpty(transaction.SetCookie))
            {
                transaction.CookieNameValue = transaction.SetCookie.Split(new[] { ';' }, 2).First();
            }
            transaction.ResponseText = await transaction.Response.Content.ReadAsStringAsync();

            if (transaction.Response.Content != null &&
                transaction.Response.Content.Headers.ContentType != null &&
                transaction.Response.Content.Headers.ContentType.MediaType == "text/xml")
            {
                transaction.ResponseElement = XElement.Parse(transaction.ResponseText);
            }
            return transaction;
        }

        private class Transaction
        {
            public HttpRequestMessage Request { get; set; }
            public HttpResponseMessage Response { get; set; }

            public string SetCookie { get; set; }
            public string CookieNameValue { get; set; }

            public string ResponseText { get; set; }
            public XElement ResponseElement { get; set; }
        }
    }
}
