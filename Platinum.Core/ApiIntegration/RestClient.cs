﻿using Platinum.Core.Types;
using Platinum.Core.Types.Exceptions;
using RestSharp;

namespace Platinum.Core.ApiIntegration
{
    public class RestClient : IRest
    {
        public IRestResponse Get(IRestRequest request)
        {
            RestSharp.RestClient client = new RestSharp.RestClient();
            IRestResponse response = client.Get(request);
            if (!response.IsSuccessful)
            {
                throw new RequestException(response.ErrorMessage + " Uri: " + response.ResponseUri ,response);
            }

            return response;
        }
        
        public IRestResponse Post(IRestRequest request)
        {
            RestSharp.RestClient client = new RestSharp.RestClient();
            IRestResponse response = client.Post(request);
            if (!response.IsSuccessful)
            {
                throw new RequestException(response.ErrorMessage + " Uri: " + response.ResponseUri ,response);
            }

            return response;
        }
    }
}