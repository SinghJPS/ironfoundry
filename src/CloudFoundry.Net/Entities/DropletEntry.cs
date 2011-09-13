﻿namespace CloudFoundry.Net.Types.Entities
{
    using Newtonsoft.Json;

    public class DropletEntry : JsonBase
    {
        [JsonProperty(PropertyName = "droplet")]
        public uint Droplet { get; set; }

        [JsonProperty(PropertyName = "instances")]
        public InstanceEntry[] Instances;
    }
}