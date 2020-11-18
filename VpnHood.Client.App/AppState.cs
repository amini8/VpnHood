﻿using System;
using System.Text.Json.Serialization;

namespace VpnHood.Client.App
{
    public class AppState
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ClientState ConnectionState { get; internal set; }
        public string LastError { get; internal set; }
        public Guid? ActiveClientProfileId { get; internal set; }
        public bool IsIdle { get; internal set; }
    }
}