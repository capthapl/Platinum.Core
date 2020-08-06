﻿using System;
using NLog;
using Platinum.Core.DatabaseIntegration;
using Platinum.Core.Types;

namespace Platinum.Service.BufforUrlQueue
{
    public class AllegroBufforUrlQueue : IBufforUrlQueueTask
    {
        Logger _logger = LogManager.GetCurrentClassLogger();

        public void Run(IDal db)
        {
            try
            {
                db.BeginTransaction();
                SelectAndMoveOffersFromBufforToOffers(db);
                db.CommitTransaction();
            }
            catch (Exception ex)
            {
                _logger.Info(ex.Message + "\n" + ex.StackTrace);
                db.RollbackTransaction();
            }
        }

        public void SelectAndMoveOffersFromBufforToOffers(IDal db)
        {
            try
            {
                int ret = db.ExecuteNonQuery(@"
                WITH OFFERS_BUFF(Id,WebsiteId,UriHash,CreatedDate,WebsiteCategoryId) as
                (
                    SELECT max(Id),WebsiteId,UriHash,MAX(CreatedDate),WebsiteCategoryId FROM offersBuffor WITH (NOLOCK)
                    GROUP BY WebsiteId,WebsiteCategoryId,UriHash,WebsiteCategoryId
                ) 
                INSERT INTO offers (WebsiteId,Uri,UriHash,CreatedDate,WebsiteCategoryId,Processed)
                SELECT 
	                OFFERS_BUFF.WebsiteId,
	                offersBuffor.Uri,
	                OFFERS_BUFF.UriHash,
	                OFFERS_BUFF.CreatedDate,
	                OFFERS_BUFF.WebsiteCategoryId,
	                0 AS Processed 
                FROM OFFERS_BUFF WITH (NOLOCK)
                INNER JOIN offersBuffor WITH(NOLOCK) ON offersBuffor.Id = OFFERS_BUFF.Id
                where OFFERS_BUFF.UriHash NOT IN (SELECT UriHash FROM Offers WITH (NOLOCK))
                ");

                _logger.Info("Moved " + ret + " offers");
                PopOffersFromBuffer(db);
                _logger.Info("Cleared buffor");
            }
            catch (Exception ex)
            {
                _logger.Info(ex.Message);
                throw;
            }
        }

        public void PopOffersFromBuffer(IDal db)
        {
            db.ExecuteNonQuery($"DELETE FROM offersBuffor;");
        }
    }
}