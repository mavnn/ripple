﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using FubuCore;
using FubuCore.CommandLine;
using FubuCore.Descriptions;
using FubuCore.Logging;
using ripple.Local;
using ripple.Model;
using ripple.New.Commands;
using ripple.New.Nuget;

namespace ripple.New.Model
{
	public enum SolutionMode
	{
		Ripple,
		Classic
	}

	[XmlType("ripple")]
	public class Solution : DescribesItself, LogTopic
	{
		private readonly IList<Project> _projects = new List<Project>();
		private readonly IList<Feed> _feeds = new List<Feed>();
		private readonly IList<Dependency> _configuredDependencies = new List<Dependency>();
		private readonly Lazy<IEnumerable<Dependency>> _missing;
		private readonly Lazy<IEnumerable<IRemoteNuget>> _updates;
		private readonly Lazy<IEnumerable<NugetSpec>> _specifications;
		private Lazy<DependencyCollection> _dependencies;
		private readonly IList<NugetSpec> _nugetDependencies = new List<NugetSpec>();
		private string _path;

		public Solution()
		{
			NugetSpecFolder = "packaging/nuget";
			SourceFolder = "src";
			BuildCommand = "rake";
			FastBuildCommand = "rake compile";
			Mode = SolutionMode.Ripple;

			AddFeed(Feed.Fubu);
			AddFeed(Feed.NuGetV2);
			AddFeed(Feed.NuGetV1);

			UseStorage(NugetStorage.Basic());
			UseFeedService(new FeedService());
			UseCache(NugetFolderCache.DefaultFor(this));
			UsePublisher(PublishingService.For(Mode));

			_missing = new Lazy<IEnumerable<Dependency>>(() => Storage.MissingFiles(this));
			_updates = new Lazy<IEnumerable<IRemoteNuget>>(findUpdates);
			_specifications = new Lazy<IEnumerable<NugetSpec>>(() => Publisher.SpecificationsFor(this));
			
			resetDependencies();
		}

		public string Name { get; set; }
		public string Directory { get; set; }
		public string NugetSpecFolder { get; set; }
		public string SourceFolder { get; set; }
		public string BuildCommand { get; set; }
		public string FastBuildCommand { get; set; }
		public SolutionMode Mode { get; set; }

		public string Path
		{
			get { return _path; }
			set
			{
				_path = value;
				if (value.IsNotEmpty() && File.Exists(value))
				{
					Directory = System.IO.Path.GetDirectoryName(value);
				}
			}
		}

		public void ConvertTo(SolutionMode mode)
		{
			Mode = mode;
			Storage.Reset(this);
			UseStorage(NugetStorage.For(mode));
		}

		[XmlIgnore]
		public INugetStorage Storage { get; private set; }
		[XmlIgnore]
		public IFeedService FeedService { get; private set; }
		[XmlIgnore]
		public INugetCache Cache { get; private set; }
		[XmlIgnore]
		public IPublishingService Publisher { get; private set; }

		private void resetDependencies()
		{
			_dependencies = new Lazy<DependencyCollection>(combineDependencies);
		}

		private DependencyCollection combineDependencies()
		{
			var dependencies = new DependencyCollection(_configuredDependencies);
			Projects.Each(p => dependencies.AddChild(p.Dependencies));
			return dependencies;
		}

		public string PackagesDirectory()
		{
			return System.IO.Path.Combine(SourceFolder, "packages").ToFullPath();
		}

		public void UsePublisher(IPublishingService service)
		{
			Publisher = service;
		}

		public void UseStorage(INugetStorage storage)
		{
			Storage = storage;
		}

		public void UseFeedService(IFeedService service)
		{
			FeedService = service;
		}

		public void UseCache(INugetCache cache)
		{
			Cache = cache;
		}

		[XmlIgnore]
		public Project[] Projects
		{
			get { return _projects.ToArray(); }
			set
			{
				_projects.Clear();
				_projects.AddRange(value);
			}
		}

		public Feed[] Feeds
		{
			get { return _feeds.ToArray(); }
			set
			{
				_feeds.Clear();
				_feeds.AddRange(value);
			}
		}

		public Dependency[] Nugets
		{
			get { return _configuredDependencies.ToArray(); }
			set
			{
				_configuredDependencies.Clear();
				_configuredDependencies.AddRange(value);
			}
		}

		[XmlIgnore]
		public DependencyCollection Dependencies
		{
			get { return _dependencies.Value; }
		}

		[XmlIgnore]
		public IEnumerable<NugetSpec> Specifications
		{
			get { return _specifications.Value; }
		}

		public void AddFeed(Feed feed)
		{
			_feeds.Fill(feed);
		}

		public void AddProject(Project project)
		{
			project.Solution = this;
			_projects.Fill(project);
		}

		public Project AddProject(string name)
		{
			var project = new Project(name);
			AddProject(project);

			return project;
		}

		public void AddDependency(Dependency dependency)
		{
			resetDependencies();
			_configuredDependencies.Fill(dependency);
		}

		public Dependency FindDependency(string name)
		{
			return _configuredDependencies.SingleOrDefault(x => x.Name == name);
		}

		public void ClearFeeds()
		{
			_feeds.Clear();
		}

		public IEnumerable<Dependency> MissingNugets()
		{
			return _missing.Value;
		}

		public IRemoteNuget Restore(Dependency dependency)
		{
			return FeedService.NugetFor(this, dependency);
		}

		public Project FindProject(string name)
		{
			return _projects.SingleOrDefault(x => x.Name.EqualsIgnoreCase(name));
		}

		public void Describe(Description description)
		{
			description.Title = "Solution \"{0}\"".ToFormat(Name);
			description.ShortDescription = Path;

			var configured = description.AddList("SolutionLevel", _configuredDependencies.OrderBy(x => x.Name));
			configured.Label = "Solution-Level";

			var feedsList = description.AddList("Feeds", Feeds);
			feedsList.Label = "NuGet Feeds";

			var projectsList = description.AddList("Projects", Projects);
			projectsList.Label = "Projects";

			var local = LocalDependencies();
			if (local.Any())
			{
				var localList = description.AddList("Local", local.All());
				localList.Label = "Local";
			}

			var missing = MissingNugets();
			if (missing.Any())
			{
				var missingList = description.AddList("Missing", missing);
				missingList.Label = "Missing";
			}
		}

		public void AssertIsValid()
		{
			var exception = new RippleException(this);

			_projects
				.SelectMany(x => x.Dependencies)
				.GroupBy(x => x.Name)
				.Each(group =>
				{
					var version = group.First().Version;
					if (group.Any(d => d.Version != version))
					{
						exception.AddProblem("Validation", "Multiple dependencies found for " + group.Key);
					}
				});

			if (exception.HasProblems())
			{
				throw exception;
			}
		}

		public void Clean(CleanMode mode)
		{
			Storage.Clean(this, mode);
		}

		public LocalDependencies LocalDependencies()
		{
			return Storage.Dependencies(this);
		}

		public IEnumerable<IRemoteNuget> Updates()
		{
			return _updates.Value;
		}

		public void Update(INugetFile nuget)
		{
			Dependencies.Update(Dependency.For(nuget));
		}

		private IEnumerable<IRemoteNuget> findUpdates()
		{
			return FeedService.UpdatesFor(this);
		}

		public void DetermineNugetDependencies(Func<string, NugetSpec> finder)
		{
			Dependencies.Each(x =>
			{
				var spec = finder(x.Name);
				if (spec != null)
				{
					_nugetDependencies.Add(spec);
				}
			});
		}

		public bool DependsOn(Solution peer)
		{
			return _nugetDependencies.Any(x => x.Publisher == peer);
		}

		public string NugetFolderFor(NugetSpec spec)
		{
			return NugetFolderFor(spec.Name);
		}

		public string NugetFolderFor(string nugetName)
		{
			var nuget = LocalDependencies().Get(nugetName);
			return nuget.NugetFolder(this);
		}

		public IEnumerable<NugetSpec> NugetDependencies
		{
			get { return _nugetDependencies; }
		}

		public IEnumerable<Solution> SolutionDependencies()
		{
			return _nugetDependencies.Select(x => x.Publisher)
				.Distinct()
				.OrderBy(x => x.Name);
		}

		public void Save()
		{
			Storage.Write(this);
			Projects.Each(Storage.Write);
		}

		public ProcessStartInfo CreateBuildProcess(bool fast)
		{
			var cmdLine = fast ? FastBuildCommand : BuildCommand;
			var commands = StringTokenizer.Tokenize(cmdLine);

			var fileName = commands.First();
			if (fileName == "rake")
			{
				fileName = RippleFileSystem.RakeRunnerFile();
			}

			return new ProcessStartInfo(fileName)
			{
				WorkingDirectory = Directory,
				Arguments = commands.Skip(1).Join(" ")
			};
		}

		public static Solution Empty()
		{
			var solution = new Solution();
			solution.ClearFeeds();

			return solution;
		}

		public static Solution For(SolutionInput input)
		{
			var builder = SolutionBuilder.For(input.ModeFlag);

			// TODO -- Need to allow a specific solution
			return builder.Build();
		}
	}
}