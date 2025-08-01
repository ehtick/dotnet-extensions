﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI.Evaluation.Utilities;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI.Evaluation.Quality;

/// <summary>
/// An <see cref="IEvaluator"/> that evaluates an AI system's performance in retrieving information for additional
/// context in response to a user request (for example, in a Retrieval Augmented Generation (RAG) scenario).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RetrievalEvaluator"/> measures the degree to which the information present in the context chunks
/// supplied via <see cref="RetrievalEvaluatorContext.RetrievedContextChunks"/> are relevant to the user request, and
/// how well these chunks are ranked (with the most relevant information appearing before less relevant information).
/// It returns a <see cref="NumericMetric"/> that contains a score for 'Retrieval'. The score is a number between 1 and
/// 5, with 1 indicating a poor score, and 5 indicating an excellent score.
/// </para>
/// <para>
/// High retrieval scores indicate that the AI system has successfully extracted and ranked the most relevant
/// information at the top, without introducing bias from external knowledge and ignoring factual correctness.
/// Conversely, low retrieval scores suggest that the AI system has failed to surface the most relevant context chunks
/// at the top of the list and / or introduced bias and ignored factual correctness.
/// </para>
/// <para>
/// <b>Note:</b> <see cref="RetrievalEvaluator"/> is an AI-based evaluator that uses an AI model to perform its
/// evaluation. While the prompt that this evaluator uses to perform its evaluation is designed to be model-agnostic,
/// the performance of this prompt (and the resulting evaluation) can vary depending on the model used, and can be
/// especially poor when a smaller / local model is used.
/// </para>
/// <para>
/// The prompt that <see cref="RetrievalEvaluator"/> uses has been tested against (and tuned to work well with) the
/// following models. So, using this evaluator with a model from the following list is likely to produce the best
/// results. (The model to be used can be configured via <see cref="ChatConfiguration.ChatClient"/>.)
/// </para>
/// <para>
/// <b>GPT-4o</b>
/// </para>
/// </remarks>
public sealed class RetrievalEvaluator : IEvaluator
{
    /// <summary>
    /// Gets the <see cref="EvaluationMetric.Name"/> of the <see cref="NumericMetric"/> returned by
    /// <see cref="RetrievalEvaluator"/>.
    /// </summary>
    public static string RetrievalMetricName => "Retrieval";

    /// <inheritdoc/>
    public IReadOnlyCollection<string> EvaluationMetricNames { get; } = [RetrievalMetricName];

    private static readonly ChatOptions _chatOptions =
        new ChatOptions
        {
            Temperature = 0.0f,
            MaxOutputTokens = 1600,
            TopP = 1.0f,
            PresencePenalty = 0.0f,
            FrequencyPenalty = 0.0f,
            ResponseFormat = ChatResponseFormat.Text
        };

    /// <inheritdoc/>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(chatConfiguration);

        var metric = new NumericMetric(RetrievalMetricName);
        var result = new EvaluationResult(metric);
        metric.MarkAsBuiltIn();

        if (!messages.TryGetUserRequest(out ChatMessage? userRequest) || string.IsNullOrWhiteSpace(userRequest.Text))
        {
            metric.AddDiagnostics(
                EvaluationDiagnostic.Error(
                    $"The {nameof(messages)} supplied for evaluation did not contain a user request as the last message."));

            return result;
        }

        if (additionalContext?.OfType<RetrievalEvaluatorContext>().FirstOrDefault()
                is not RetrievalEvaluatorContext context)
        {
            metric.AddDiagnostics(
                EvaluationDiagnostic.Error(
                    $"A value of type {nameof(RetrievalEvaluatorContext)} was not found in the {nameof(additionalContext)} collection."));

            return result;
        }

        if (context.RetrievedContextChunks.Count is 0)
        {
            metric.AddDiagnostics(
                EvaluationDiagnostic.Error(
                    $"Supplied {nameof(RetrievalEvaluatorContext)} did not contain any {nameof(RetrievalEvaluatorContext.RetrievedContextChunks)}."));

            return result;
        }

        List<ChatMessage> evaluationInstructions = GetEvaluationInstructions(userRequest, context);

        (ChatResponse evaluationResponse, TimeSpan evaluationDuration) =
            await TimingHelper.ExecuteWithTimingAsync(() =>
                chatConfiguration.ChatClient.GetResponseAsync(
                    evaluationInstructions,
                    _chatOptions,
                    cancellationToken)).ConfigureAwait(false);

        _ = metric.TryParseEvaluationResponseWithTags(evaluationResponse, evaluationDuration);
        metric.AddOrUpdateContext(context);
        metric.Interpretation = metric.InterpretScore();
        return result;
    }

    private static List<ChatMessage> GetEvaluationInstructions(
        ChatMessage userRequest,
        RetrievalEvaluatorContext context)
    {
#pragma warning disable S103 // Lines should not be too long
        const string SystemPrompt =
            """
            # Instruction
            ## Goal
            ### You are an expert in evaluating the quality of a list of CONTEXT chunks from a query based on provided definition and data. Your goal will involve answering the questions below using the information provided.
            - **Definition**: You are given a definition of the retrieval quality that is being evaluated to help guide your Score.
            - **Data**: Your input data include QUERY and CONTEXT.
            - **Tasks**: To complete your evaluation you will be asked to evaluate the Data in different ways.
            """;
#pragma warning restore S103

        List<ChatMessage> evaluationInstructions = [new ChatMessage(ChatRole.System, SystemPrompt)];

        string renderedUserRequest = userRequest.RenderText();

        int count = 0;
        StringBuilder builder = new StringBuilder().Append('[');
        foreach (string retrievedChunk in context.RetrievedContextChunks)
        {
            _ = builder.Append('"').Append(retrievedChunk.Replace('"', '\'')).Append('"');
            if (++count < context.RetrievedContextChunks.Count)
            {
                _ = builder.Append(", ");
            }
        }

        _ = builder.Append(']');
        string renderedContext = builder.ToString();

#pragma warning disable S103 // Lines should not be too long
        string evaluationPrompt =
            $$"""
            # Definition
            **Retrieval** refers to measuring how relevant the context chunks are to address a query and how the most relevant context chunks are surfaced at the top of the list. It emphasizes the extraction and ranking of the most relevant information at the top, without introducing bias from external knowledge and ignoring factual correctness. It assesses the relevance and effectiveness of the retrieved context chunks with respect to the query.

            # Ratings
            ## [Retrieval: 1] (Irrelevant Context, External Knowledge Bias)
            **Definition:** The retrieved context chunks are not relevant to the query despite any conceptual similarities. There is no overlap between the query and the retrieved information, and no useful chunks appear in the results. They introduce external knowledge that isn't part of the retrieval documents.

            **Examples:**
              **Query:** what is kuchen?
              **Context:** ["There's nothing like the taste of a cake you made in your own kitchen. Baking a cake is as simple as measuring ingredients, mixing them in the right order, and remembering to take the cake out of the oven before it burns.", "A steady 325-350 degrees is ideal when it comes to baking pound cake. Position the pan in the middle of the oven, and rotate it once, halfway through the baking time, as it bakes to account for any hot spots. "CHOCOLATE POUND CAKE. Cream butter, sugar ... and floured bundt pan, 10 inch pan or 2 (9x5x3 inch) loaf pans. Bake at ... pans. Bake until cake tester inserted in ... to drizzle down sides. 4. BUTTERMILK LEMON POUND CAKE."", "Pour batter into your pan(s) and place in the oven. Cook for 75 minutes, checking periodically. Some ovens cook unevenly or quickly -- if this describes yours, keep an eye on it. 1 If to be used for fancy ornamented cakes, bake 30 to 35 minutes in a dripping-pan. 2 Insert a skewer or toothpick to see if it's finished.", "As a general rule of thumb you can bake most cakes at 375 degrees Fahrenheit (which is 180 degrees Celsius) and check them after about 30 minutes and expect it to take at least 45 minutes.", "Till a toothpick inserted in the center of the cake comes out clean. Depends on the heat of your oven but start checking at about 45 minutes and when the cake is golden brown. sonnyboy · 8 years ago. Thumbs up.", "1 This results in a pound cake with maximum volume. 2 Be patient. Beat softened butter (and cream cheese or vegetable shortening) at medium speed with an electric mixer until creamy. 3 This can take from 1 to 7 minutes, depending on the power of your mixer."]

              **Query:** What are the main economic impacts of global warming?
              **Context:** ["Economic theories such as supply and demand explain how prices fluctuate in a free market.", "Global warming is caused by increased carbon dioxide levels, which affect the environment and the atmosphere.", "Political factors also play a role in economic decisions across nations."]

            ## [Retrieval: 2] (Partially Relevant Context, Poor Ranking, External Knowledge Bias)
            **Definition:** The context chunks are partially relevant to address the query but are mostly irrelevant, and external knowledge or LLM bias starts influencing the context chunks. The most relevant chunks are either missing or placed at the bottom.

            **Examples:**
              **Query:** what is rappelling
              **Context:** ["5. Cancel. Rappelling is the process of coming down from a mountain that is usually done with two pieces of rope. Use a natural anchor or a set of bolts to rappel from with help from an experienced rock climber in this free video on rappelling techniques. Part of the Video Series: Rappelling & Rock Climbing.", "Abseiling (/ˈaebseɪl/ ˈæbseɪl /or/ ; ˈɑːpzaɪl From german, abseilen meaning to rope), down also called, rappelling is the controlled descent of a vertical, drop such as a rock, face using a. Rope climbers use this technique when a cliff or slope is too steep/and or dangerous to descend without. protection", "1. rappel - (mountaineering) a descent of a vertical cliff or wall made by using a doubled rope that is fixed to a higher point and wrapped around the body. abseil. mountain climbing, mountaineering-the activity of climbing a mountain. descent-the act of changing your location in a downward direction."]

              **Query:** Describe the causes of the French Revolution.
              **Context:** ["The French Revolution started due to economic disparity, leading to unrest among the lower classes.", "The Industrial Revolution also contributed to changes in society during the 18th century.", "Philosophers like Rousseau inspired revolutionary thinking, but the taxation system played a role as well."]

            ## [Retrieval: 3] (Relevant Context Ranked Bottom)
            **Definition:** The context chunks contain relevant information to address the query, but the most pertinent chunks are located at the bottom of the list.

            **Examples:**
              **Query:** what are monocytes
              **Context:** ["Monocytes are produced by the bone marrow from precursors called monoblasts, bipotent cells that differentiated from hematopoietic stem cells. Monocytes circulate in the bloodstream for about one to three days and then typically move into tissues throughout the body. Monocytes which migrate from the bloodstream to other tissues will then differentiate into tissue resident macrophages or dendritic cells. Macrophages are responsible for protecting tissues from foreign substances, but are also suspected to be important in the formation of important organs like the heart and brain.", "Report Abuse. A high level of monocytes could mean a number of things. They're a type of phagocyte-a type of cell found in your blood that 'eats' many types of attacking bacteria and other microorganisms when it matures. High levels could mean that you have an infection as more develop to fight it.", "Our immune system has a key component called the white blood cells, of which there are several different kinds. Monocytes are a type of white blood cell that fights off bacteria, viruses and fungi. Monocytes are the biggest type of white blood cell in the immune system. Originally formed in the bone marrow, they are released into our blood and tissues. When certain germs enter the body, they quickly rush to the site for attack.", "Monocyte. Monocytes are produced by the bone marrow from stem cell precursors called monoblasts. Monocytes circulate in the bloodstream for about one to three days and then typically move into tissues throughout the body. They make up three to eight percent of the leukocytes in the blood. Monocyte under a light microscope (40x) from a peripheral blood smear surrounded by red blood cells. Monocytes are a type of white blood cell, part of the human body's immune system. They are usually identified in stained smears by their large two-lobed nucleus.", "A monocyte (pictured below) is a large type of white blood cell with one large, smooth, well-defined, indented, slightly folded, oval, kidney-shaped, or notched nucleus (the cell's control center). White blood cells help protect the body against diseases and fight infections.", "Monocytes are white blood cells that are common to the blood of all vertebrates and they help the immune system to function properly. There are a number of reasons for a high monocyte count, which can also be called monocytosis. Some of the reasons can include stress, viral fevers, inflammation and organ necrosis. A physician may order a monocyte blood count test to check for raised levels of monocytes. There are a number of reasons for this test, from a simple health check up to people suffering from heart attacks and leukemia. Complications with the blood and cancer are two other reasons that this test may be performed.", "Monocytes are considered the largest white blood cell. These cells are part of the innate immune system. Monocytes also play important roles in the immune function of the body. These cells are often found when doing a stained smear and appear large kidney shaped. Many of these are found in the spleen area.", "This is taken directly from-http://www.wisegeek.com/what-are-monocytes.htm#. Monocytes are a type of leukocyte or white blood cell which play a role in immune system function. Depending on a patient's level of health, monocytes make up between one and three percent of the total white blood cells in the body. For example, if monocytes are elevated because of an inflammation caused by a viral infection, the patient would be given medication to kill the virus and bring down the inflammation. Typically, when a monocyte count is requested, the lab will also run other tests on the blood to generate a complete picture.", "3D Rendering of a Monocyte. Monocytes are a type of white blood cells (leukocytes). They are the largest of all leukocytes. They are part of the innate immune system of vertebrates including all mammals (humans included), birds, reptiles, and fish. Monocytes which migrate from the bloodstream to other tissues will then differentiate into tissue resident macrophages or dendritic cells. Macrophages are responsible for protecting tissues from foreign substances, but are also suspected to be important in the formation of important organs like the heart and brain."]

              **Query:** What were the key features of the Magna Carta?
              **Context:** ["The Magna Carta influenced the legal system in Europe, especially in constitutional law.", "It was signed in 1215 by King John of England to limit the powers of the monarchy.", "The Magna Carta introduced principles like due process and habeas corpus, which are key features of modern legal systems."]

            ## [Retrieval: 4] (Relevant Context Ranked Middle, No External Knowledge Bias and Factual Accuracy Ignored)
            **Definition:** The context chunks fully address the query, but the most relevant chunk is ranked in the middle of the list. No external knowledge is used to influence the ranking of the chunks; the system only relies on the provided context. Factual accuracy remains out of scope for evaluation.

            **Examples:**
              **Query:** do game shows pay their contestants
              **Context:** ["So, in the end, game show winners get some of the money that TV advertisers pay to the networks, who pay the show producers, who then pay the game show winners. Just in the same way that the actors, and crew of a show get paid. Game shows, like other programs, have costs to produce the programs—they have to pay for sets, cameras, talent (the hosts), and also prizes to contestants.", "(Valerie Macon/Getty Images). Oh, happy day! You're a contestant on a popular game show—The Price Is Right, let's say. You spin the wheel, you make the winning bid, and suddenly—ka-ching!—you've won the Lexus or the dishwasher or the lifetime supply of nail clippers.", "1 If you can use most of the prizes the show offers, such as a new car or trip, you may be content to appear on a game show that features material prizes. 2 If not, you should probably try out for a show where cash is the main prize. 3 In the United States, game show contestants must pay taxes on any prizes they win. 2. Meet the eligibility requirements. All game shows have certain eligibility requirements for their contestants. Generally, you must be at least 18 years of age, except for those shows that use child or teenage contestants, and you are allowed to appear on no more than 1 game show per year.", "Rating Newest Oldest. Best Answer: You don't always win the money amount on the front of your lectern when you are on a game show. As someone else said, 2nd place earns $2000 and 3rd place earns $1000 in Jeopardy! In any case, the prize money is paid out from the ad revenue that the show receives from sponsors. I think in this case Who Wants to be a Millionaire or Deal or No Deal is the best example of how shows can be successful while still paying the prize money. I feel this way because these shows have a potential, however small it may be, to pay out 1 million dollars to every contestant on the show. Here is the reality. Regardless of the show whether it be a game show or a drama, a network will receive money from commercial advertising based on the viewership. With this in mind a game show costs very little to actually air compared to a full production drama series, that's where the prize money comes from"]

            ## [Retrieval: 5] (Highly Relevant, Well Ranked, No Bias Introduced)
            **Definition:** The context chunks not only fully address the query, but also surface the most relevant chunks at the top of the list. The retrieval respects the internal context, avoids relying on any outside knowledge, and focuses solely on pulling the most useful content to the forefront, irrespective of the factual correctness of the information.

            **Examples:**
              **Query:** The smallest blood vessels in your body, where gas exchange occurs are called
              **Context:** ["Gas exchange is the delivery of oxygen from the lungs to the bloodstream, and the elimination of carbon dioxide from the bloodstream to the lungs. It occurs in the lungs between the alveoli and a network of tiny blood vessels called capillaries, which are located in the walls of the alveoli. The walls of the alveoli actually share a membrane with the capillaries in which oxygen and carbon dioxide move freely between the respiratory system and the bloodstream.", "Arterioles branch into capillaries, the smallest of all blood vessels. Capillaries are the sites of nutrient and waste exchange between the blood and body cells. Capillaries are microscopic vessels that join the arterial system with the venous system.", "Arterioles are the smallest arteries and regulate blood flow into capillary beds through vasoconstriction and vasodilation. Capillaries are the smallest vessels and allow for exchange of substances between the blood and interstitial fluid. Continuous capillaries are most common and allow passage of fluids and small solutes. Fenestrated capillaries are more permeable to fluids and solutes than continuous capillaries.", "Tweet. The smallest blood vessels in the human body are capillaries. They are responsible for the absorption of oxygen into the blood stream and for removing the deoxygenated red blood cells for return to the heart and lungs for reoxygenation.", "2. Capillaries—these are the sites of gas exchange between the tissues. 3. Veins—these return oxygen poor blood to the heart, except for the vein that carries blood from the lungs. On the right is a diagram showing how the three connect. Notice the artery and vein are much larger than the capillaries.", "Gas exchange occurs in the capillaries which are the smallest blood vessels in the body. Each artery that comes from the heart is surrounded by capillaries so that they can take it to the various parts of the body."]


            # Data
            QUERY: {{renderedUserRequest}}
            CONTEXT: {{renderedContext}}


            # Tasks
            ## Please provide your assessment Score for the previous CONTEXT in relation to the QUERY based on the Definitions above. Your output should include the following information:
            - **ThoughtChain**: To improve the reasoning process, think step by step and include a step-by-step explanation of your thought process as you analyze the data based on the definitions. Keep it brief and start your ThoughtChain with "Let's think step by step:".
            - **Explanation**: a very short explanation of why you think the input Data should get that Score.
            - **Score**: based on your previous analysis, provide your Score. The Score you give MUST be a integer score (i.e., "1", "2"...) based on the levels of the definitions.


            ## Please provide your answers between the tags: <S0>your chain of thoughts</S0>, <S1>your explanation</S1>, <S2>your Score</S2>.
            # Output
            """;
#pragma warning restore S103

        evaluationInstructions.Add(new ChatMessage(ChatRole.User, evaluationPrompt));

        return evaluationInstructions;
    }
}
