import { groups, BenchmarkEvent, onBenchmarkEvent, group } from './lib/minibench/minibench.js';
import './renderList.js';
import './jsonHandling.js';
import './orgChart.js';

new HtmlUI('E2E Performance', '#display');

if (location.href.indexOf('#stress') !== -1) {
  (async function() {
    const query = new URLSearchParams(window.location.search);
    const resultsUrl = query.get('resultsUrl');

    groups.forEach(async g => await g.runSetup());

    while (true) {
      groups.forEach(g => g.runAll({ skipGroupSetup: true, skipGroupTeardown: true }));

      const scenarioResults = [];
      await new Promise((resolve, reject) => {
        onBenchmarkEvent(async (status, args) => {
          switch (status) {
            case BenchmarkEvent.runStarted:
              scenarioResults.length = 0;
              break;
            case BenchmarkEvent.benchmarkCompleted:
              console.log(`Completed benchmark ${args.name}`);
              scenarioResults.push(args);
              break;
            case BenchmarkEvent.benchmarkError:
              reject(new Error('Benchmark error'));
              break;
            case BenchmarkEvent.runCompleted:
              if (resultsUrl) {
                await fetch(resultsUrl, {
                  method: 'post',
                  body: JSON.stringify({
                    scenarioResults: scenarioResults
                  })
                });
                resolve();
              }
              break;
            default:
              reject(new Error(`Unknown status: ${status}`));
          }
        });
      });
    }
  })();
}
