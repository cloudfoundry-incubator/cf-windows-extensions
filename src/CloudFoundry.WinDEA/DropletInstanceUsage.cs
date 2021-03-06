﻿namespace CloudFoundry.WinDEA
{
    using System;
    using CloudFoundry.Utilities;
    using CloudFoundry.Utilities.Json;

    /// <summary>
    /// This class encapsulates all resource usage for a droplet instance.
    /// </summary>
    public class DropletInstanceUsage : JsonConvertibleObject
    {
        /// <summary>
        /// Gets or sets the total process ticks at that time.
        /// </summary>
        public long TotalProcessTicks
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the used memory kbytes.
        /// </summary>
        [JsonName("mem")]
        public long MemoryBytes
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the cpu.
        /// </summary>
        [JsonName("cpu")]
        public float Cpu
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the disk bytes.
        /// </summary>
        [JsonName("disk")]
        public long DiskBytes
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the running time of the droplet instance as a ruby compatible string.
        /// </summary>
        [JsonName("time")]
        public string TimeInterchangeableFormat
        {
            get { return RubyCompatibility.DateTimeToRubyString(this.Time); }
            set { this.Time = RubyCompatibility.DateTimeFromRubyString(value); }
        }

        /// <summary>
        /// Gets or sets the running time of the droplet instance.
        /// </summary>
        public DateTime Time
        {
            get;
            set;
        }
    }
}
