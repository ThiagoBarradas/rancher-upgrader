﻿using Microsoft.Extensions.CommandLineUtils;
using RestSharp;
using RestSharp.Authenticators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

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
                    var ttl = config.Option("-m | --max-time", "Max Time to Wait in Seconds", CommandOptionType.SingleValue);
                    var tag = config.Option("-t | --tag", "New image tag", CommandOptionType.SingleValue);
                    var forceFinish = config.Option("-f | --force", "Force finish if upgraded", CommandOptionType.NoValue);
                    var updateEnv = config.Option("-k | --update-env", "Update environment variables", CommandOptionType.NoValue);
                    var wait = config.Option("-w | --wait", "Wait complete", CommandOptionType.NoValue);
                    var envs = config.Option("-e | --env", "Setup env var", CommandOptionType.MultipleValue);

                    config.HelpOption(HELP_ARGS);
                    config.OnExecute(() =>
                    {
                        var options = new Options(action, url, user, pass, image, tag, updateEnv, wait, forceFinish, envs, ttl);

                        if (options.ReturnCode == 1)
                        {
                            options.ErrorMessages.ForEach(r => Console.WriteLine(" - {0}", r));
                            return 1;
                        }
                        else
                        {
                            var optionsSplitted = options.Split();

                            if (optionsSplitted.Count() == 1)
                            {
                                return Execute(options);
                            }

                            List<Options> results = new List<Options>();
                            int code = 0;

                            Parallel.ForEach(optionsSplitted, new ParallelOptions { MaxDegreeOfParallelism = 20 }, opt =>
                            {
                                var tempCode = Execute(opt);
                                code += tempCode;
                            });

                            return code == 0 ? 0 : 1;
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
            var returnCode = 0;
            var state = "active";
            Console.WriteLine("");
            switch (opts.Action)
            {
                case "upgrade":
                    state = "upgraded";
                    returnCode = Upgrade(opts);
                    break;
                case "finishupgrade":
                    returnCode = Finish(opts);
                    break;
                case "rollback":
                    returnCode = Rollback(opts);
                    break;
                default:
                    Console.Write(" - Invalid action, try use upgrade, finishupgrade or rollback");
                    return 1;
            }

            if (opts.Wait)
            {
                var maxWait = opts.Ttl * 60;

                Console.Write($"Waiting {state} state.");
                dynamic waitResult;
                int count = 0;
                do
                {
                    count++;
                    Thread.Sleep(1000);
                    Console.Write(".");
                    waitResult = Execute(opts, Method.GET, null);
                }
                while (waitResult["state"] != state && waitResult["transitioning"] == "yes" && count <= maxWait);

                if (opts.Action != "rollback")
                {
                    if (waitResult["state"] != state &&
                        (waitResult["transitioning"] != "yes" || count > maxWait))
                    {
                        Console.WriteLine("Force rollback...");
                        var clone = new Options(opts);
                        clone.Action = "rollback";
                        clone.Wait = true;
                        Execute(clone);
                        Console.WriteLine("Finished!");
                        throw new TimeoutException(waitResult["transitioningMessage"]);
                    }

                    if (waitResult["state"] != state)
                    {
                        throw new TimeoutException($"Waiting {state} state failed!");
                    }
                }

                Console.WriteLine();
            }

            return returnCode;
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
                var clone = new Options(opts);
                clone.Action = "finishupgrade";
                clone.Wait = true;
                Execute(clone);
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
                Console.WriteLine("Exception | {0}", opts.Url);
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

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity &&
                opts.Action != "upgrade")
            {
                return response.Data;
            }

            if (statusCodes.Contains(response.StatusCode) == false)
            {
                Console.WriteLine("Invalid StatusCode | {0}", opts.Url);
                Console.WriteLine(response.StatusDescription);
                throw new Exception();
            }

            return response.Data;
        }
    }

    public class Options
    {
        public const int DEFAULT_TTL = 10;

        public Options(Options opts)
        {
            this.Action = opts.Action;
            this.EnvironmentVars = opts.EnvironmentVars;
            this.ErrorMessages = opts.ErrorMessages;
            this.Force = opts.Force;
            this.Image = opts.Image;
            this.Tag = opts.Tag;
            this.ReturnCode = opts.ReturnCode;
            this.Pass = opts.Pass;
            this.User = opts.User;
            this.Url = opts.Url;
            this.Wait = opts.Wait;
            this.UpdateEnvironment = opts.UpdateEnvironment;
            this.Ttl = opts.Ttl;
        }

        public Options(CommandArgument action, CommandOption url, CommandOption user, CommandOption pass, CommandOption image, CommandOption tag, CommandOption updateEnv, CommandOption wait, CommandOption force, CommandOption envs, CommandOption ttl)
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
            try
            {
                var ttlValue = ttl.Value();
                if (!string.IsNullOrWhiteSpace(ttlValue))
                {
                    this.Ttl = int.Parse(ttlValue);
                }
                else
                {
                    this.Ttl = DEFAULT_TTL;
                }
            }
            catch (Exception)
            {
                this.Ttl = DEFAULT_TTL;
            }
        }

        public void AddError(string message)
        {
            this.ReturnCode = 1;
            this.ErrorMessages.Add(message);
        }

        public bool IsMultiple()
        {
            return this.Url.Contains("|");
        }

        public List<Options> Split()
        {
            if (!this.IsMultiple())
            {
                return new List<Options> { this };
            }

            var urls = this.Url.Split("|");

            return urls.Select(url =>
            {
                var temp = new Options(this);
                temp.Url = url;
                return temp;
            }).ToList();
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

        public int Ttl { get; set; }

        public List<string> EnvironmentVars { get; set; }
    }
}