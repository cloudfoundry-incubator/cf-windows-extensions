﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Uhuru.CloudFoundry.Server.MSSqlNode
{
    /// <summary>
    /// This is a class containing information about connecting to an MS Sql Server.
    /// </summary>
    public class MSSqlOptions
    {

        /// <summary>
        /// Gets or sets the host for connecting to the service.
        /// </summary>
        public string Host
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the user for connecting to the SQL Server.
        /// </summary>
        public string User
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the password for connection to the SQL Server.
        /// </summary>
        public string Password
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the port for connection to the SQL Server.
        /// </summary>
        public int Port
        {
            get;
            set;
        }

    }
}
