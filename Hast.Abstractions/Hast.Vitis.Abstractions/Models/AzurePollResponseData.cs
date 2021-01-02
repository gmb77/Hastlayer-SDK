﻿using System.Collections.Generic;

namespace Hast.Vitis.Abstractions.Models
{
    public class AzurePollResponseData
    {
        public string OrchestrationStatus { get; set; }
        public IEnumerable<string> OrchestrationOutput { get; set; }

        public void Deconstruct(out string status, out IEnumerable<string> output)
        {
            status = OrchestrationStatus;
            output = OrchestrationOutput;
        }
    }

    public enum OrchestrationStatus
    {
        Pending,
        Running,
        Failed,
        Successful,
    }
}
