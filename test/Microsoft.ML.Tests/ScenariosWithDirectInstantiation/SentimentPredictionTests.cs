﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.ML.Models;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Api;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.FastTree;
using Microsoft.ML.Runtime.Internal.Calibration;
using Microsoft.ML.Runtime.Model;
using Microsoft.ML.Trainers;
using Microsoft.ML.Transforms;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Microsoft.ML.Scenarios
{
    public partial class ScenariosTests
    {
        [Fact]
        public void TrainAndPredictSentimentModelWithDirectionInstantiationTest()
        {
            var dataPath = GetDataPath(SentimentDataPath);
            var testDataPath = GetDataPath(SentimentTestPath);

            using (var env = new TlcEnvironment(seed: 1, conc: 1))
            {
                // Pipeline
                var loader = new TextLoader(env,
                new TextLoader.Arguments()
                {
                    Separator = "tab",
                    HasHeader = true,
                    Column = new[]
                    {
                        new TextLoader.Column()
                        {
                            Name = "Label",
                            Source = new [] { new TextLoader.Range() { Min=0, Max=0} },
                            Type = DataKind.Num
                        },

                        new TextLoader.Column()
                        {
                            Name = "SentimentText",
                            Source = new [] { new TextLoader.Range() { Min=1, Max=1} },
                            Type = DataKind.Text
                        }
                    }
                }, new MultiFileSource(dataPath));

                var trans = TextTransform.Create(env, new TextTransform.Arguments()
                {
                    Column = new TextTransform.Column
                    {
                        Name = "Features",
                        Source = new[] { "SentimentText" }
                    },
                    KeepDiacritics = false,
                    KeepPunctuations = false,
                    TextCase = Runtime.TextAnalytics.TextNormalizerTransform.CaseNormalizationMode.Lower,
                    OutputTokens = true,
                    StopWordsRemover = new Runtime.TextAnalytics.PredefinedStopWordsRemoverFactory(),
                    VectorNormalizer = TextTransform.TextNormKind.L2,
                    CharFeatureExtractor = new NgramExtractorTransform.NgramExtractorArguments() { NgramLength = 3, AllLengths = false },
                    WordFeatureExtractor = new NgramExtractorTransform.NgramExtractorArguments() { NgramLength = 2, AllLengths = true },
                },
                loader);

                // Train
                var trainer = new FastTreeBinaryClassificationTrainer(env, new FastTreeBinaryClassificationTrainer.Arguments()
                {
                    NumLeaves = 5,
                    NumTrees = 5,
                    MinDocumentsInLeafs = 2
                });

                var trainRoles = TrainUtils.CreateExamples(trans, label: "Label", feature: "Features");
                trainer.Train(trainRoles);

                // Get scorer and evaluate the predictions from test data
                var pred = trainer.CreatePredictor();
                IDataScorerTransform testDataScorer = GetScorer(env, trans, pred, testDataPath);
                var metrics = EvaluateBinary(env, testDataScorer);
                ValidateBinaryMetrics(metrics);

                // Create prediction engine and test predictions
                var model = env.CreateBatchPredictionEngine<SentimentData, SentimentPrediction>(testDataScorer);
                var sentiments = GetTestData();
                var predictions = model.Predict(sentiments, false);
                Assert.Equal(2, predictions.Count());
                Assert.True(predictions.ElementAt(0).Sentiment.IsFalse);
                Assert.True(predictions.ElementAt(1).Sentiment.IsTrue);

                // Get feature importance based on feature gain during training
                var summary = ((FeatureWeightsCalibratedPredictor)pred).GetSummaryInKeyValuePairs(trainRoles.Schema);
                Assert.Equal(1.0, (double)summary[0].Value, 1);
            }
        }

        private BinaryClassificationMetrics EvaluateBinary(IHostEnvironment env, IDataView scoredData)
        {
            var dataEval = TrainUtils.CreateExamplesOpt(scoredData, label: "Label", feature: "Features");

            // Evaluate.
            // It does not work. It throws error "Failed to find 'Score' column" when Evaluate is called
            //var evaluator = new BinaryClassifierEvaluator(env, new BinaryClassifierEvaluator.Arguments());

            var evaluator = new BinaryClassifierMamlEvaluator(env, new BinaryClassifierMamlEvaluator.Arguments());
            var metricsDic = evaluator.Evaluate(dataEval);

            return BinaryClassificationMetrics.FromMetrics(env, metricsDic["OverallMetrics"], metricsDic["ConfusionMatrix"])[0];
        }
    }
}
