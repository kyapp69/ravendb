import viewModelBase = require("viewmodels/viewModelBase");
import getDatabaseStatsCommand = require("commands/resources/getDatabaseStatsCommand");
import getIndexesStatsCommand = require("commands/database/index/getIndexesStatsCommand");
import shell = require("viewmodels/shell");
import changesContext = require("common/changesContext");
import changeSubscription = require('common/changeSubscription');
import optional = require("common/optional");
import appUrl = require("common/appUrl");

import statsModel = require("models/database/stats/statistics");

class statistics extends viewModelBase {

    stats = ko.observable<statsModel>();
    
    private refreshStatsObservable = ko.observable<number>();
    private statsSubscription: KnockoutSubscription;

    attached() {
        super.attached();
        this.statsSubscription = this.refreshStatsObservable.throttle(3000).subscribe((e) => this.fetchStats());
        this.fetchStats();
        this.updateHelpLink('H6GYYL');
    }

    detached() {
        super.detached();

        if (this.statsSubscription != null) {
            this.statsSubscription.dispose();
        }
    }
   
    fetchStats(): JQueryPromise<Raven.Client.Data.DatabaseStatistics> {
        var db = this.activeDatabase();

        const dbStatsTask = new getDatabaseStatsCommand(db)
            .execute();

        const indexesStatsTask = new getIndexesStatsCommand(db)
            .execute();

        return $.when<any>(dbStatsTask, indexesStatsTask)
            .done(([dbStats]: [Raven.Client.Data.DatabaseStatistics], [indexesStats]: [Raven.Client.Data.Indexes.IndexStats[]]) => {
                this.processStatsResults(dbStats, indexesStats);
                });
    }

    afterClientApiConnected(): void {
        const changesApi = this.changesContext.resourceChangesApi();
        this.addNotification(changesApi.watchAllDocs(e => this.refreshStatsObservable(new Date().getTime())));
        //TODO: this.addNotification(changesApi.watchAllIndexes((e) => this.refreshStatsObservable(new Date().getTime())))
    }

    processStatsResults(dbStats: Raven.Client.Data.DatabaseStatistics, indexesStats: Raven.Client.Data.Indexes.IndexStats[]) {
        this.stats(new statsModel(dbStats, indexesStats));
    }
    
    urlForIndexPerformance(indexName: string) {
        return appUrl.forIndexPerformance(this.activeDatabase(), indexName);
    }
}

export = statistics;
