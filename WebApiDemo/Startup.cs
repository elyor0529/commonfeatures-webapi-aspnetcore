﻿using AutoMapper;
using FluentValidation;
using FluentValidation.AspNetCore;
using ImpromptuInterface;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Serilog;
using Swashbuckle.AspNetCore.Swagger;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using WebApiDemo.AuthorizationHandlers;
using WebApiDemo.Database;
using WebApiDemo.Extensions;
using WebApiDemo.HttpClients;
using WebApiDemo.Middlewares;
using WebApiDemo.Models;
using WebApiDemo.Providers;
using WebApiDemo.Repositories;
using WebApiDemo.Services;
using WebApiDemo.Services.Tenants;
using WebApiDemo.Validators;
using Microsoft.EntityFrameworkCore.SqlServer;
using Microsoft.EntityFrameworkCore;
using WebApiDemo.HealthCheck;

namespace WebApiDemo
{
    public class Startup
    {
        public Startup(IConfiguration configuration, ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
        {
            // Init Serilog configuration
            Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();
            Configuration = configuration;
            LoggerFactory = loggerFactory;
            ServiceProvider = serviceProvider;

            TypesToRegister = Assembly.Load("WebApiDemo")
                                      .GetTypes()
                                      .Where(x => !string.IsNullOrEmpty(x.Namespace))
                                      .Where(x => x.IsClass)
                                      .Where(x => x.Namespace.StartsWith("WebApiDemo.Services.Tenants")).ToList();
        }

        public IConfiguration Configuration { get; }

        public ILoggerFactory LoggerFactory { get; }

        public IServiceProvider ServiceProvider { get; }

        public List<Type> TypesToRegister { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            #region DemoAuthentication
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(options =>
            {
                options.Authority = "https://login.microsoftonline.com/136544d9-038e-4646-afff-10accb370679";
                options.Audience = "257b6c36-1168-4aac-be93-6f2cd81cec43";
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            });
            #endregion

            #region DemoAuthorization
            services.AddAuthorization(opts =>
            {
                opts.AddPolicy("SurveyCreator", p =>
                {
                    // Using value text for demo show, else use enum : ClaimTypes.Role
                    p.RequireClaim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "SurveyCreator");

                });

                opts.AddPolicy("SuperSurveyCreator", p =>
                {
                    // Using value text for demo show, else use enum : ClaimTypes.Role
                    //p.RequireClaim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "SurveyCreator");
                    //p.RequireClaim("groups", "8115e3be-ac7a-4886-a1e6-5b6aaf810a8f");
                    p.Requirements.Add(new SuperSurveyCreatorRequirement("SurveyCreator", "8115e3be-ac7a-4886-a1e6-5b6aaf810a8f"));
                });
            });

            // Authorization handlers
            services.AddSingleton<IAuthorizationHandler, SuperSurveyCreatorAutorizationHandler>();
            #endregion

            #region Demo Validator
            services.AddSingleton<IValidator<User>, UserValidator>();
            #endregion

            #region DemoConfig
            var config = new {
                ConnectionString = ServiceProvider.GetService<IConfiguration>()["MySecretConnectionString"]
            }
            .ActLike<IConfig>(); // <-- from Azure Keyvault

            services.AddScoped<IMyRepository>(c =>
            {
                return new MyRepository(config);
            });
            #endregion

            #region DemoCRUD EF + ORMLite
            //services.AddScoped<ICountryRepository>(c =>
            //{
            //    return new OrmLiteCountryRepository(config);
            //});
            services.AddDbContext<DemoDbContext>(options => options.UseSqlServer(config.ConnectionString));
            services.AddScoped<ICountryRepository, EFCountryRepository>();
            #endregion

            #region DemoHealthCheck
            services.AddHealthChecks()
            .AddCheck("MyDatabase", new SqlConnectionHealthCheck(config.ConnectionString));
            #endregion

            #region DemoCache
            services.AddMemoryCache();
            #endregion

            #region DemoResponseCaching
            // caching response for middlewares
            //services.AddResponseCaching();
            #endregion

            #region DemoMapping Automapper
            services.AddAutoMapper();
            #endregion

            #region MVC + FluentValidation
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2).AddFluentValidation();
            #endregion

            #region override modelstate for fluentvalidation
            services.Configure<ApiBehaviorOptions>(options =>
            {
                options.InvalidModelStateResponseFactory = (context) =>
                {
                    var errors = context.ModelState.Values.SelectMany(x => x.Errors.Select(p => p.ErrorMessage)).ToList();
                    var result = new
                    {
                        Code = "00009",
                        Message = "Validation errors",
                        Errors = errors
                    };
                    return new BadRequestObjectResult(result);
                };
            });
            #endregion

            #region DemoDocumenting
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new Info { Title = "My API + profiler integrated on top left page", Version = "v1" });
                c.AddSecurityDefinition("Bearer", new ApiKeyScheme
                {
                    Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                    Name = "Authorization",
                    In = "header",
                    Type = "apiKey"
                });
                c.AddSecurityRequirement(new Dictionary<string, IEnumerable<string>>
                {
                    { "Bearer", new string[] { } }
                });
            });
            #endregion

            #region DemoProfiling
            services.AddMiniProfiler(options =>
                options.RouteBasePath = "/profiler"
            );
            #endregion

            #region DemoHttpClient
            services.AddHttpClient<IDataClient, DataClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost:56190/api/");
            })
            .AddPolicyHandlers("PolicyConfig", LoggerFactory, Configuration);

            services.AddHttpClient<IStreamingClient, StreamingClient>(client =>
            {
                client.BaseAddress = new Uri("https://anthonygiretti.blob.core.windows.net/videos/");
            });
            #endregion

            #region DemoCompression
            services.AddResponseCompression(options =>
            {
                options.Providers.Add<GzipCompressionProvider>();
                options.EnableForHttps = true;
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/x-sql", "video/mp4" });
            });

            services.Configure<GzipCompressionProviderOptions>(options =>
            {
                options.Level = CompressionLevel.Optimal;
            });
            #endregion

            #region DemoApiVersionning
            services.AddApiVersioning(options =>
            {
                options.ReportApiVersions = true;
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.DefaultApiVersion = new ApiVersion(1, 0);
            });
            #endregion

            #region DemoMultiTenant
            // Classes to register
            TypesToRegister.ForEach(x => services.AddScoped(x));
            // Multitenant interface with its related classes
            services.AddScopedDynamic<ITenantService>(TypesToRegister);

            // Global Service provider
            services.AddScoped(typeof(IServicesProvider<>), typeof(ServicesProvider<>));
            #endregion
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // caching all response that resturn 200 ok
            //app.UseResponseCaching();

            #region MiniProfiler
            // profiling, url to see last profile check: http://localhost:62258/profiler/results
            app.UseMiniProfiler();
            #endregion

            #region Documenting
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.RoutePrefix = "api-doc";
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
                // index.html customizable downloadable here: https://github.com/domaindrivendev/Swashbuckle.AspNetCore/blob/master/src/Swashbuckle.AspNetCore.SwaggerUI/index.html
                // this custom html has miniprofiler integration
                c.IndexStream = () => GetType().GetTypeInfo().Assembly.GetManifestResourceStream("WebApiDemo.SwaggerIndex.html");
            });
            #endregion

            #region Logging
            loggerFactory.AddSerilog();
            #endregion

            #region Authenticating
            app.UseAuthentication();
            #endregion

            #region Middlewares
            // global caching
            //app.UseMiddleware<CachingMiddleware>();

            // global exception handling
            app.UseMiddleware<CustomExceptionMiddleware>();
            #endregion

            #region HealthCheck
            app.UseHealthChecks("/health", new HealthCheckOptions()
            {
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [HealthStatus.Degraded] = StatusCodes.Status200OK,
                    [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
                }
            });
            #endregion

            #region Compression
            //app.UseResponseCompression();
            #endregion

            // mini profiler 
            //app.UseMiddleware<MiniProfilerMiddleware>();

            app.UseMvc();
        }
    }
}