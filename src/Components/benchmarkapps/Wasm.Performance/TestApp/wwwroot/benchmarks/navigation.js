import { group, benchmark, setup, teardown } from './lib/minibench/minibench.js';
import { BlazorApp } from './util/BlazorApp.js';
import { receiveEvent } from './util/BenchmarkEvents.js';

group('Navigation', () => {
  let app;

  setup(async () => {
    app = new BlazorApp();
    await app.start();
  });

  teardown(() => app.dispose());

  // measures time to navigate between rendered components with different UI, use timers, implement IDisposables etc
  benchmark('Time to navigate', () =>
    benchmarkNavigation(app), {
    descriptor: {
      name: 'blazorwasm/navigation',
      description: 'Navigation - Time in ms'
    }
  });
});

async function benchmarkNavigation(app) {
  const appDocument = app.window.document;
  app.navigateTo('renderList');
  let nextCompletion = receiveEvent('Finished rendering list');
  appDocument.querySelector('#show-list').click();
  await nextCompletion;

  app.navigateTo('orgChart');
  nextCompletion = receiveEvent('Finished OrgChart rendering');
  appDocument.querySelector('#show').click();
  await nextCompletion;

  nextCompletion = receiveEvent('Finished updating color');
  app.navigateTo('timer');
  await nextCompletion;
}
