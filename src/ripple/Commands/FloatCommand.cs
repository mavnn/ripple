using System;
using System.ComponentModel;
using FubuCore.CommandLine;
using System.Collections.Generic;
using ripple.New.Commands;

namespace ripple.Commands
{
    public class NugetInput : SolutionInput
    {
        [Description("The name of the nuget to allow to float")]
        public string Name { get; set; }
    }

    [CommandDescription("Allows a nuget to 'float' and be automatically updated from the Update command")]
    public class FloatCommand : FubuCommand<NugetInput>
    {
        public override bool Execute(NugetInput input)
        {
			input.EachSolution(solution =>
			{
				solution.Dependencies.Find(input.Name).Float();
				solution.Save();
			});

            return true;
        }
    }

    [CommandDescription("Locks a nuget to prevent it from being automatically updated from the Update command")]
    public class LockCommand : FubuCommand<NugetInput>
    {
        public override bool Execute(NugetInput input)
        {
			throw new NotImplementedException();
            /*input.FindSolutions().Each(solution =>
            {
                solution.AlterConfig(config => config.LockNuget(input.Name));
            });

            return true;*/
        }
    }
}