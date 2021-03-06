﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using HtmlAgilityPack;
using NLog;
using Platinum.Core.ApiIntegration;
using Platinum.Core.DatabaseIntegration;
using Platinum.Core.ElasticIntegration;
using Platinum.Core.Model;
using Platinum.Core.Types;
using Platinum.Core.Types.Exceptions;
using RestSharp;

namespace Platinum.Core.OfferListController
{
    public class HttpAllegroOfferListController: HttpClientInstance, IBaseOfferListController
    {
        private const string baseUrl = "https://allegro.pl";
        private string pageId;
        private OfferCategory initiedOfferCategory;
        private string urlArgs = "";
        readonly private Logger logger = LogManager.GetCurrentClassLogger();
        private int webApiUser;
        private int lastPageNumber = 0;

        public HttpAllegroOfferListController() : base()
        {
            lastPageNumber = 0;
            urlArgs = "";
            initiedOfferCategory = null;
        }


        public void StartFetching(bool fetchJustFirstPage, OfferCategory category, List<WebsiteCategoriesFilterSearch> urlArguments = null, int webApiUser = 0)
        {
            this.webApiUser = webApiUser;
            StartFetching(fetchJustFirstPage,category,urlArguments);
        }

        public void StartFetching(bool fetchJustFirstPage, OfferCategory category,
            List<WebsiteCategoriesFilterSearch> urlArguments = null)
        {
            logger.Info("Started fetching category: " + category.CategoryName);
            initiedOfferCategory = category;
            if (urlArguments != null && urlArguments.Any(x => x.WebsiteCategoryId != category.CategoryId))
                throw new OfferListControllerException("Url argument do not fit to page", this);
            
            if (urlArguments != null && urlArguments.Count > 0)
            {
                int index = 0;
                foreach (var arg in urlArguments)
                {
                    if (index == 0)
                    {
                        urlArgs += "?" + arg.Argument + "=" + arg.Value;
                        index++;
                    }
                    else
                    {
                        urlArgs += "&" + arg.Argument + "=" + arg.Value;
                    }
                }
            }

            RunFetching(fetchJustFirstPage);
        }

        [ExcludeFromCodeCoverage]
        private void RunFetching(bool fetchJustFirstPage)
        {
            if (fetchJustFirstPage)
            {
                OpenUrl(baseUrl + "/" + initiedOfferCategory.CategoryUrl + urlArgs);
                IEnumerable<string> offerLinks = GetAllOfferLinks();
                UpdateDatabaseWithOffers(offerLinks);
            }
            else
            {
                logger.Info("Append to open page :" +baseUrl + "/" + initiedOfferCategory.CategoryUrl + urlArgs);
                OpenUrl(baseUrl + "/" + initiedOfferCategory.CategoryUrl + urlArgs);
                IEnumerable<string> offerLinks = GetAllOfferLinks();
                UpdateDatabaseWithOffers(offerLinks);

                try
                {
                    while (OpenNextPage())
                    {
                        offerLinks = GetAllOfferLinks();
                        UpdateDatabaseWithOffers(offerLinks);
                    }
                }
                catch (OfferListControllerException ex)
                {
                    logger.Error(ex);
                }
            }
        }

        [ExcludeFromCodeCoverage]
        public bool OpenNextPage()
        {
            try
            {
                int currentPage = GetCurrentPageIndex();
                if (lastPageNumber != 0 && currentPage == lastPageNumber)
                {
                    logger.Info($"ERROR - Last page index is same as current. BREAK {lastPageNumber} ~ {currentPage}");
                    return false;
                }
                int lastPage = GetLastPageIndex();
                logger.Info($"Append to open next page - current page {currentPage} / last page {lastPage}");
                if (currentPage >= lastPage)
                {
                    return false;
                }

                if (urlArgs.Length > 1)
                {
                    lastPageNumber = currentPage;
                    OpenUrl(baseUrl + "/" + initiedOfferCategory.CategoryUrl + urlArgs + "&p=" + (currentPage + 1));
                }
                else
                {
                    lastPageNumber = currentPage;
                    OpenUrl(baseUrl + "/" + initiedOfferCategory.CategoryUrl + "?p=" + (currentPage + 1));
                }

                if (lastPage == currentPage + 1)
                    return false;
                
                return true;
            }
            catch (Exception ex)
            {
                throw new OfferListControllerException("Cannot open next page, check browser is initied "+ex.Message, this);
            }
        }

        public IEnumerable<string> GetAllOfferLinks()
        {
            string pageSource = LastResponse; 
            HtmlDocument document = new HtmlDocument();
            document.LoadHtml(pageSource);

            var offerContainer = document.DocumentNode.SelectNodes("//div[@id=\"opbox-listing--base\"]");
            if (offerContainer == null)
            {
                offerContainer = document.DocumentNode.SelectNodes("//div[@data-box-name=\"items container\"]");
            }
            if (offerContainer == null || !offerContainer.Any())
            {
                throw new OfferListControllerException("Allegro layout has been changed", this);
            }
            else
            {
                HtmlNodeCollection offerLinks = offerContainer.First().SelectNodes("//a");

                List<HtmlNode> offerLinksNode = offerLinks.Where(x => x.HasAttributes).ToList();
                logger.Info("offerl links: "+ offerLinksNode.Count);

                foreach (var offerLink in offerLinksNode)
                {
                    if (offerLink.Attributes["href"] != null &&
                        offerLink.Attributes["href"].Value.Contains("/oferta/") &&
                        offerLink.Attributes["href"].Value.Contains("http"))
                        yield return offerLink.Attributes["href"].Value;
                }
            }
        }

        public void UpdateDatabaseWithOffers(IEnumerable<string> offers)
        {
#if DEBUG
            using (Dal db = new Dal(true))
#endif
#if RELEASE
            using (Dal db = new Dal(false))
#endif
            {
                System.Diagnostics.Debug.WriteLine("Updating database");
                logger.Info("Updating database buffor with offers");
                string query = $@"INSERT INTO [dbo].offersBuffor VALUES ";
                List<string> enumerable = offers.ToList();
                List<string> uniqueOffers = new List<string>();
                for (int i = 0; i < enumerable.Count; i++)
                {
                    logger.Info("Check offer exist in buffor");
                    bool offerBuffored = ElasticController.Instance.OfferExistsInBuffor(enumerable.ElementAt(i));
                    if (!offerBuffored)
                    {
                        logger.Info("Offer not exist in buffor - insert");
                        bool successInsert = ElasticController.Instance.InsertOffer(enumerable.ElementAt(i));
                        if(successInsert)
                            uniqueOffers.Add(enumerable.ElementAt(i));
                    }
                    else
                    {
                        logger.Info("Offer exist in buffor - skip");
                    }
                }
                int offerCount = uniqueOffers.Count();
                if (offerCount == 0) return;
                List<string> queryValues = new List<string>();
                foreach (string offer in uniqueOffers)
                {
                    if (!offer.Contains("\'"))
                    {
                        queryValues.Add($@"
                        (
                            {(int) EOfferWebsite.Allegro}
                            ,'{offer}'
                            ,HashBytes('MD5','{offer}')
                            , getdate()
                            , {initiedOfferCategory.CategoryId},
                            {webApiUser} 
                        )");
                    }
                }
                logger.Info($"Execute insert to buffor for {uniqueOffers} offers");
                db.ExecuteNonQuery(query + string.Join(",",queryValues));
                logger.Info($"Buffer updated for {uniqueOffers.Count()} offers");
            }
        }

        public int GetCurrentPageIndex()
        {
            HtmlNodeCollection paginationContainer;
            int validationCounter = 0;
            do
            {
                string pageSource = LastResponse;
                HtmlDocument document = new HtmlDocument();
                document.LoadHtml(pageSource);
                paginationContainer =
                    document.DocumentNode.SelectNodes("//*[@aria-label=\"paginacja\"]");
                validationCounter++;
                if (validationCounter > 1)
                {
                    Thread.Sleep(2500);
                    OpenUrl(LastRequestedUrl);
                }

                if (validationCounter == 10)
                {
                    throw new OfferListControllerException(
                        "Cannot fetch max page index. Allegro layout could be changed",
                        this);
                }
            } while (!ValidatePaginationContainer(paginationContainer));


            HtmlNode maxPageInput = paginationContainer.First().ChildNodes.First(x => x.Name == "input");

            return int.Parse(maxPageInput.GetAttributeValue("value", "default"));
        }

        public int GetLastPageIndex()
        {
            HtmlNodeCollection paginationContainer;

            int validationCounter = 0;
            do

            {
               
                HtmlDocument document = new HtmlDocument();
                document.LoadHtml(LastResponse);
                paginationContainer =
                    document.DocumentNode.SelectNodes("//*[@aria-label=\"paginacja\"]");
                validationCounter++;
                if (validationCounter > 1)
                {
                    Thread.Sleep(2500);
                    OpenUrl(LastRequestedUrl);
                }

                if (validationCounter == 10)
                {
                    throw new OfferListControllerException(
                        "Cannot fetch max page index. Allegro layout could be changed",
                        this);
                }

                Thread.Sleep(5000);
            } while (!ValidatePaginationContainer(paginationContainer));


            HtmlNode maxPageInput = paginationContainer.First().ChildNodes.First(x => x.Name == "input");

            HtmlAttribute maxPage = maxPageInput.Attributes
                .First(x => x.Name.Equals("data-maxpage"));

            return int.Parse(maxPage.Value);
        }

        public void DeInit()
        {
        }

        public void Dispose()
        {
        }

        private bool ValidatePaginationContainer(HtmlNodeCollection collection)
        {
            return !(collection == null || !collection.Any());
        }
    }
}