﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Raven.Abstractions;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Database.Server.Security.OAuth;

namespace Raven.Database.Server.Controllers
{
	public class OAuthController : RavenApiController
	{
		const string TokenContentType = "application/json; charset=UTF-8";
		const string TokenGrantType = "client_credentials";
		private const int MaxOAuthContentLength = 1500;
		private static readonly TimeSpan MaxChallengeAge = TimeSpan.FromMinutes(10);

		[Import]
		public IAuthenticateClient AuthenticateClient { get; set; }

		private int numberOfTokensIssued;
		public int NumberOfTokensIssued
		{
			get { return numberOfTokensIssued; }
		}

		[HttpGet][Route("OAuth/AccessToken")]
		public HttpResponseMessage AccessTokenGet()
		{
			if (GetHeader("Accept") != TokenContentType)
			{
				return
					GetMessageWithObject(new {error = "invalid_request", error_description = "Accept should be: " + TokenContentType},
						HttpStatusCode.BadRequest);
			}

			if (GetHeader("grant_type") != TokenGrantType)
			{
				return
					GetMessageWithObject(new { error = "unsupported_grant_type", error_description = "Only supported grant_type is: " + TokenGrantType },
						HttpStatusCode.BadRequest);
			}

			var identity = GetUserAndPassword();

			if (identity == null)
			{
				var msg =
					GetMessageWithObject(new {error = "invalid_client", error_description = "No client authentication was provided"},
						HttpStatusCode.Unauthorized);
				msg.Headers.Add("WWW-Authenticate", "Basic realm=\"Raven DB\"");
				return msg;
			}

			List<DatabaseAccess> authorizedDatabases;
			if (!AuthenticateClient.Authenticate(Database, identity.Item1, identity.Item2, out authorizedDatabases))
			{
				if ((Database == DatabasesLandlord.SystemDatabase ||
					 !AuthenticateClient.Authenticate(DatabasesLandlord.SystemDatabase, identity.Item1, identity.Item2, out authorizedDatabases)))
				{
					var msg =
					GetMessageWithObject(new { error = "unauthorized_client", error_description = "Invalid client credentials" },
						HttpStatusCode.Unauthorized);
					msg.Headers.Add("WWW-Authenticate", "Basic realm=\"Raven DB\"");
					return msg;
				}
			}

			Interlocked.Increment(ref numberOfTokensIssued);

			var userId = identity.Item1;

			var token = AccessToken.Create(DatabasesLandlord.SystemConfiguration.OAuthTokenKey, new AccessTokenBody
			{
				UserId = userId,
				AuthorizedDatabases = authorizedDatabases
			});

			return GetMessageWithObject(token.Serialize());
		}

		[HttpPost][Route("OAuth/API-Key")]
		public async Task<HttpResponseMessage> ApiKeyPost()
		{
			if (InnerRequest.Content.Headers.ContentLength > MaxOAuthContentLength)
			{
				return
					GetMessageWithObject(
						new
						{
							error = "invalid_request",
							error_description = "Content length should not be over " + MaxOAuthContentLength + " bytes"
						},
						HttpStatusCode.BadRequest);
			}

			if (InnerRequest.Content.Headers.ContentLength == 0)
			{
				return RespondWithChallenge();
			}

			string requestContents;
			//using (var reader = new StreamReader(context.Request.InputStream))
			//	requestContents = reader.ReadToEnd();
			requestContents = await ReadStringAsync();

			var requestContentsDictionary = OAuthHelper.ParseDictionary(requestContents);
			var rsaExponent = requestContentsDictionary.GetOrDefault(OAuthHelper.Keys.RSAExponent);
			var rsaModulus = requestContentsDictionary.GetOrDefault(OAuthHelper.Keys.RSAModulus);
			if (rsaExponent == null || rsaModulus == null ||
				!rsaExponent.SequenceEqual(OAuthServerHelper.RSAExponent) || !rsaModulus.SequenceEqual(OAuthServerHelper.RSAModulus))
			{
				return RespondWithChallenge();
			}

			var encryptedData = requestContentsDictionary.GetOrDefault(OAuthHelper.Keys.EncryptedData);
			if (string.IsNullOrEmpty(encryptedData))
			{
				return RespondWithChallenge();
			}

			var challengeDictionary = OAuthHelper.ParseDictionary(OAuthServerHelper.DecryptAsymmetric(encryptedData));
			var apiKeyName = challengeDictionary.GetOrDefault(OAuthHelper.Keys.APIKeyName);
			var challenge = challengeDictionary.GetOrDefault(OAuthHelper.Keys.Challenge);
			var response = challengeDictionary.GetOrDefault(OAuthHelper.Keys.Response);

			if (string.IsNullOrEmpty(apiKeyName) || string.IsNullOrEmpty(challenge) || string.IsNullOrEmpty(response))
			{
				return RespondWithChallenge();
			}

			var challengeData = OAuthHelper.ParseDictionary(OAuthServerHelper.DecryptSymmetric(challenge));
			var timestampStr = challengeData.GetOrDefault(OAuthHelper.Keys.ChallengeTimestamp);
			if (string.IsNullOrEmpty(timestampStr))
			{
				return RespondWithChallenge();
				
			}

			var challengeTimestamp = OAuthServerHelper.ParseDateTime(timestampStr);
			if (challengeTimestamp + MaxChallengeAge < SystemTime.UtcNow || challengeTimestamp > SystemTime.UtcNow)
			{
				// The challenge is either old or from the future 
				return RespondWithChallenge();
			}

			var apiKeyTuple = GetApiKeySecret(apiKeyName);
			if (apiKeyTuple == null)
			{
				return GetMessageWithObject(new {error = "unauthorized_client", error_description = "Unknown API Key"},
					HttpStatusCode.Unauthorized);
			}

			var apiSecret = apiKeyTuple.Item1;
			if (string.IsNullOrEmpty(apiKeyName))
			{
				return GetMessageWithObject(new {error = "unauthorized_client", error_description = "Invalid API Key"},
					HttpStatusCode.Unauthorized);
			}

			var expectedResponse = OAuthHelper.Hash(string.Format(OAuthHelper.Keys.ResponseFormat, challenge, apiSecret));
			if (response != expectedResponse)
			{
				return GetMessageWithObject(new {error = "unauthorized_client", error_description = "Invalid challenge response"},
					HttpStatusCode.Unauthorized);
			}

			var token = apiKeyTuple.Item2;

			return GetMessageWithObject(token.Serialize());
		}

		private Tuple<string, string> GetUserAndPassword()
		{
			if (User != null)
			{
				var httpListenerBasicIdentity = User.Identity as HttpListenerBasicIdentity;
				if (httpListenerBasicIdentity != null)
				{
					return Tuple.Create(httpListenerBasicIdentity.Name, httpListenerBasicIdentity.Password);
				}
			}

			var auth = GetHeader("Authorization");
			if (string.IsNullOrEmpty(auth) || auth.StartsWith("Basic", StringComparison.OrdinalIgnoreCase) == false)
				return null;

			var userAndPass = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Substring("Basic ".Length)));
			var parts = userAndPass.Split(':');
			if (parts.Length != 2)
				return null;

			return Tuple.Create(parts[0], parts[1]);
		}

		public HttpResponseMessage RespondWithChallenge()
		{
			var challengeData = new Dictionary<string, string>
			{
				{ OAuthHelper.Keys.ChallengeTimestamp, OAuthServerHelper.DateTimeToString(SystemTime.UtcNow) },
				{ OAuthHelper.Keys.ChallengeSalt, OAuthHelper.BytesToString(OAuthServerHelper.RandomBytes(OAuthHelper.Keys.ChallengeSaltLength)) }
			};

			var responseData = new Dictionary<string, string>
			{
				{ OAuthHelper.Keys.RSAExponent, OAuthServerHelper.RSAExponent },
				{ OAuthHelper.Keys.RSAModulus, OAuthServerHelper.RSAModulus },
				{ OAuthHelper.Keys.Challenge, OAuthServerHelper.EncryptSymmetric(OAuthHelper.DictionaryToString(challengeData)) }
			};
			var msg = GetEmptyMessage(HttpStatusCode.PreconditionFailed);
			msg.Headers.Add("WWW-Authenticate", OAuthHelper.Keys.WWWAuthenticateHeaderKey + " " + OAuthHelper.DictionaryToString(responseData));

			return msg;
		}

		private Tuple<string, AccessToken> GetApiKeySecret(string apiKeyName)
		{
			var document = DatabasesLandlord.SystemDatabase.Get("Raven/ApiKeys/" + apiKeyName, null);
			if (document == null)
				return null;

			var apiKeyDefinition = document.DataAsJson.JsonDeserialization<ApiKeyDefinition>();
			if (apiKeyDefinition.Enabled == false)
				return null;

			return Tuple.Create(apiKeyDefinition.Secret, AccessToken.Create(DatabasesLandlord.SystemConfiguration.OAuthTokenKey, new AccessTokenBody
			{
				UserId = apiKeyName,
				AuthorizedDatabases = apiKeyDefinition.Databases
			}));
		}
	}
}
