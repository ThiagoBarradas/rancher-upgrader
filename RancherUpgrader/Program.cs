using Microsoft.Extensions.CommandLineUtils;
using RestSharp;
using RestSharp.Authenticators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace RancherUpgrader
{
    public static class Program
    {
        const string HELP_ARGS = "-? | -h | --help";

        static int Main(string[] args)
        {
            Console.WriteLine("##############################");
            Console.WriteLine("#      RANCHER UPGRADER!     #");
            Console.WriteLine("##############################");

            try
            {
                var cli = new CommandLineApplication
                { Description = "Rancher Upgrader - Upgrade, rollback and finish upgrades." };

                cli.OnExecute(() =>
                {
                    cli.ShowHelp();
                    return 0;
                });

                var execute = cli.Command("execute", config =>
                {
                    config.Description = "Execute operation";

                    var action = config.Argument("action", "Action like 'upgrade' 'finishupgrade' 'rollback'", false);
                    var url = config.Option("-r | --url", "API url for rancher service", CommandOptionType.SingleValue);
                    var user = config.Option("-u | --user", "User for authentication with rancher API", CommandOptionType.SingleValue);
                    var pass = config.Option("-p | --pass", "Pass for authentication with rancher API", CommandOptionType.SingleValue);
                    var image = config.Option("-i | --image", "New image", CommandOptionType.SingleValue);
                    var tag = config.Option("-t | --tag", "New image tag", CommandOptionType.SingleValue);
                    var forceFinish = config.Option("-f | --force", "Force finish if upgraded", CommandOptionType.NoValue);
                    var updateEnv = config.Option("-k | --update-env", "Update environment variables", CommandOptionType.NoValue);
                    var wait = config.Option("-w | --wait", "Wait complete", CommandOptionType.NoValue);
                    var envs = config.Option("-e | --env", "Setup env var", CommandOptionType.MultipleValue);

                    config.HelpOption(HELP_ARGS);
                    config.OnExecute(() =>
                    {
                        var options = new Options(action, url, user, pass, image, tag, updateEnv, wait, forceFinish, envs);

                        if (options.ReturnCode == 1)
                        {
                            options.ErrorMessages.ForEach(r => Console.WriteLine(" - {0}", r));
                            return 1;
                        }
                        else
                        {
                            return Execute(options);
                        }
                    });
                });

                cli.HelpOption(HELP_ARGS);
                cli.Execute(args);

                return 0;
            }
            catch(Exception e)
            {
                Console.WriteLine("Ooops! Exception: {0}", e.Message);
                return 1;
            }
        }

        private static int Execute(Options opts)
        {
            switch (opts.Action)
            {
                case "upgrade":
                    return Upgrade(opts);
                case "finishupgrade":
                    return Finish(opts);
                case "rollback":
                    return Rollback(opts);
                default:
                    Console.Write(" - Invalid action, try use upgrade, finishupgrade or rollback");
                    return 1;
            }
        }

        private static int Finish(Options opts)
        {
            Execute(opts, Method.POST, null);
            Console.WriteLine("Finish upgrade called!");
            return 0;
        }

        private static int Upgrade(Options opts)
        {
            var result = Execute(opts, Method.GET, null);

            var launchConfig = result["launchConfig"];

            if (opts.Force && result["state"] == "upgraded")
            {
                Console.WriteLine("Force finish...");
                var clone = (Options) opts.Clone();
                clone.Action = "finishrollback";
                clone.Wait = true;
                Finish(clone);
                Console.WriteLine("Finished!");
            }

            if (string.IsNullOrWhiteSpace(opts.Tag) == false)
            {
                var splitted = ((string)launchConfig["imageUuid"]).Split(':');

                if (splitted.Count() == 1)
                {
                    launchConfig["imageUuid"] = splitted[0] + ":" + opts.Tag;
                }
                else
                {
                    splitted[splitted.Count() - 1] = opts.Tag;
                    launchConfig["imageUuid"] = string.Join(':', splitted);
                }
            }

            if (string.IsNullOrWhiteSpace(opts.Image) == false)
            {
                var splitted = ((string)launchConfig["imageUuid"]).Split(':');

                if (splitted.Count() == 1)
                {
                    launchConfig["imageUuid"] = opts.Image;
                }
                else
                {
                    splitted[splitted.Count() - 2] = opts.Image;
                    launchConfig["imageUuid"] = string.Join(':', splitted);
                }
            }

            if (opts.UpdateEnvironment == true)
            {
                var envs = new Dictionary<string, string>();

                foreach (var env in opts.EnvironmentVars)
                {
                    var envParts = env.Split('=');
                    envs[envParts.First()] = (envParts.Count() > 1) ? string.Join("=", envParts.Skip(1)) : "";
                }

                launchConfig["environment"] = envs;
            }

            var upgradeBody = new
            {
                inServiceStrategy = new {
                    batchSize = 1,
                    intervalMillis = 2000,
                    startFirst = true,
                    launchConfig
                }
            };

            Execute(opts, Method.POST, upgradeBody);
            Console.WriteLine("Upgrade called!");
            return 0;
        }

        private static int Rollback(Options opts)
        {
            Execute(opts, Method.POST, null);
            Console.WriteLine("Rollback called!");
            return 0;
        }

        private static dynamic Execute(Options opts, Method method, object body)
        {
            RestClient client = new RestClient(opts.Url)
            {
                Authenticator = new HttpBasicAuthenticator(opts.User, opts.Pass)
            };

            var request = new RestSharp.RestRequest("", method);

            if (method != Method.GET)
            {
                request.AddQueryParameter("action", opts.Action);
            }

            if (body != null && method != Method.GET)
            {
                request.AddJsonBody(body);
            }

            var response = client.Execute<dynamic>(request);

            if (response.ErrorException != null)
            {
                Console.WriteLine("Exception:");
                Console.WriteLine(response.ErrorException.Message);
                throw new Exception();
            }

            var statusCodes = new List<HttpStatusCode>
            {
                HttpStatusCode.OK,
                HttpStatusCode.Created,
                HttpStatusCode.Accepted,
                HttpStatusCode.NoContent
            };

            if (statusCodes.Contains(response.StatusCode) == false)
            {
                Console.WriteLine("Invalid StatusCode");
                Console.WriteLine(response.StatusDescription);
                throw new Exception();
            }

            if (opts.Wait)
            {
                Console.Write("Waiting active state.");
                dynamic waitResult;
                do
                {
                    Thread.Sleep(1000);
                    Console.Write(".");
                    waitResult = Execute(opts, Method.GET, null);
                }
                while (waitResult["state"] != "active");
                Console.WriteLine();
            }

            return response.Data;
        }
    }

    public class Options : ICloneable
    {
        public Options() { }

        public Options(CommandArgument action, CommandOption url, CommandOption user, CommandOption pass, CommandOption image, CommandOption tag, CommandOption updateEnv, CommandOption wait, CommandOption force, CommandOption envs)
        {
            this.ErrorMessages = new List<string>();
            this.ReturnCode = 0;

            this.Action = action.Value;

            if (url.HasValue() == false)
            {
                this.AddError("'url' is required");
            }
            else
            {
                this.Url = url.Value();
            }

            this.User = user.Value();
            this.Pass = pass.Value();
            this.Tag = tag.Value();
            this.Image = image.Value();
            this.UpdateEnvironment = updateEnv.HasValue();
            this.Wait = wait.HasValue();
            this.Force = force.HasValue();
            this.EnvironmentVars = envs.Values.ToList();
        }

        public void AddError(string message)
        {
            this.ReturnCode = 1;
            this.ErrorMessages.Add(message);
        }

        public object Clone()
        {
            return (Options) this.MemberwiseClone();
        }

        public int ReturnCode { get; set; }

        public List<string> ErrorMessages { get; set; }

        public string Url { get; set; }

        public string Action { get; set; }

        public string User { get; set; }

        public string Pass { get; set; }

        public string Image { get; set; }

        public string Tag { get; set; }

        public bool UpdateEnvironment { get; set; }

        public bool Wait { get; set; }

        public bool Force { get; set; }

        public List<string> EnvironmentVars { get; set; }
    }
}