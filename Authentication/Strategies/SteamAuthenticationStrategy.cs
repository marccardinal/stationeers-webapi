
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ceen;
using WebAPI.Payloads;
using WebAPI.Server.Exceptions;

namespace WebAPI.Authentication.Strategies
{
    public class SteamAuthenticationStrategy : IAuthenticationStrategy
    {
        private const string ProviderUri = @"https://steamcommunity.com/openid/login";

        private static readonly Regex SteamIdRegex = new Regex(@"openid\/id\/([0-9]{17,25})", RegexOptions.Compiled);
        private static readonly Regex IsValidRegex = new Regex(@"(is_valid\s*:\s*true)", RegexOptions.Compiled);

        public async Task<ApiUser> TryAuthenticate(IHttpContext context)
        {
            if (context.Request.Method != "GET")
            {
                // openid requests use GET
                throw new MethodNotAllowedException();
            }

            var queryString = context.Request.QueryString;

            if (queryString.Count == 0)
            {
                // Redirect to steam's openid endpoint
                var queryParams = new Dictionary<string, string>() {
                    {"openid.ns", "http://specs.openid.net/auth/2.0"},
                    {"openid.mode", "checkid_setup"},
                    // Normally, we would have the response come back to us, but we are using jwt and not session data.
                    //  This means the client has to make the call to us, not steam, as the client will need to receive the
                    //  resulting authorization header.
                    // These are left blank, and the client must fill them in on its own.
                    //{"openid.return_to", string.Format("{0}://{1}:{2}/login", Config.Protocol, Server.IP, Config.Port)},
                    //{"openid.realm", string.Format("{0}://{1}:{2}", Config.Protocol, Server.IP, Config.Port)},
                    {"openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"},
                    {"openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select"}
                };
                var query = GenerateQuery(queryParams);

                // Would be nice to use TemporarilyMoved here, but thats auto handled by browsers
                //  and there is no way for the client to receive the location being redirected.
                // context.Response.Headers.Add("Location", string.Format("{0}?{1}", ProviderUri, query));
                await context.SendResponse(Ceen.HttpStatusCode.OK, new AuthenticateWithSteamPayload()
                {
                    location = string.Format("{0}?{1}", ProviderUri, query)
                });
                return null;
            }

            queryString["openid.mode"] = "check_authentication";

            var responseString = HttpPost(ProviderUri, GenerateQuery(queryString));

            var match = SteamIdRegex.Match(queryString["openid.claimed_id"]);
            ulong steamId = ulong.Parse(match.Groups[1].Value);

            match = IsValidRegex.Match(responseString);
            var isValid = match.Success;
            if (!isValid)
            {
                Logging.Log(
                    new Dictionary<string, string>() {
                        { "SteamID", steamId.ToString() }
                    },
                    "Steam rejected the provided openid credentials."
                );
                throw new ForbiddenException("Invalid Credentials.");
            }

            var allowedSteamIds = Config.AllowedSteamIds;
            if (allowedSteamIds.Length > 0 && !allowedSteamIds.Contains(steamId.ToString()))
            {
                Logging.Log(
                    new Dictionary<string, string>() {
                        { "SteamID", steamId.ToString() }
                    },
                    "Attempted login by a SteamID not in the allow list."
                );
                throw new ForbiddenException();
            }

            var user = ApiUser.MakeSteamUser(context, steamId);
            Authenticator.SetUserToken(context, user);
            return user;
        }

        public void Verify(IHttpContext context, out ApiUser user)
        {
            user = Authenticator.GetUserFromToken(context);
            if (user == null || !user.isSteamUser)
            {
                throw new UnauthorizedException();
            }

            var allowedSteamIds = Config.AllowedSteamIds;
            if (allowedSteamIds.Length > 0 && !allowedSteamIds.Contains(user.steamId))
            {
                Logging.Log(
                    new Dictionary<string, string>() {
                        { "SteamID", user.steamId }
                    },
                    "JWT login request contained a SteamID not in the allow list."
                );
                throw new ForbiddenException();
            }
        }

        private static string HttpPost(string uri, string content)
        {
            var data = Encoding.ASCII.GetBytes(content);
            var request = WebRequest.Create(ProviderUri);
            request.Method = "POST";
            request.Headers.Add("Accept-language", "en");
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = content.Length;
            request.Timeout = 6000;
            using (var stream = request.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }

            var response = (HttpWebResponse)request.GetResponse();
            return new StreamReader(response.GetResponseStream()).ReadToEnd();
        }

        private static string GenerateQuery(IDictionary<string, string> collection, bool useAmp = true)
        {
            var parts = from key in collection.Keys
                        let value = collection[key]
                        select string.Format("{0}={1}", Uri.EscapeDataString(key), Uri.EscapeDataString(value));
            return string.Join(useAmp ? "&" : "&amp;", parts);
        }
    }
}