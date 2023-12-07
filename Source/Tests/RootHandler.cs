﻿using Meadow.Foundation.Web.Maple;
using Meadow.Foundation.Web.Maple.Routing;
using System;
using System.Diagnostics;
using System.Net;

namespace Maple.Unit.Tests
{
    internal class RootHandler : IRequestHandler
    {
        public HttpListenerContext Context { get; set; }

        public bool IsReusable => true;

        public void Dispose()
        {
        }

        [HttpGet("/")]
        public void GetRoot(Guid paramName)
        {
            Debug.WriteLine($"{paramName}");
        }
    }
}
