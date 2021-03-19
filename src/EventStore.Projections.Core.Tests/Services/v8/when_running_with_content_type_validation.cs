﻿using System;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Tests.Services.projections_manager;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.v8 {
	public class when_running_with_content_type_validation {
		[TestFixture]
		public class when_running_with_content_type_validation_enabled : TestFixtureWithJsProjection {
			protected override void Given() {
				_projection = @"
                fromAll().when({$any: 
                    function(state, event) {
                    linkTo('output-stream' + event.sequenceNumber, event);
                    return {};
                }});
            ";
			}

			protected override IProjectionStateHandler CreateStateHandler() {
				return _stateHandlerFactory.Create(
					"JS", _projection, 
					enableContentTypeValidation: true, logger: (s, _) => {
						if (s.StartsWith("P:"))
							Console.WriteLine(s);
						else
							_logged.Add(s);
					}); // skip prelude debug output
			}

			[Test, Category("v8")]
			public void process_null_json_event_does_not_emit() {
				string state = null;
				EmittedEventEnvelope[] emittedEvents = null;

				var result = _stateHandler.ProcessEvent(
					"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
					"metadata",
					null, out state, out emittedEvents, isJson: true);

				Assert.IsNull(emittedEvents);
			}

			[Test, Category("v8")]
			public void process_null_non_json_event_does_emit() {
				string state = null;
				EmittedEventEnvelope[] emittedEvents = null;

				var result = _stateHandler.ProcessEvent(
					"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
					"metadata",
					null, out state, out emittedEvents, isJson: false);

				Assert.IsNotNull(emittedEvents);
				Assert.AreEqual(1, emittedEvents.Length);
			}
		}

		[TestFixture]
		public class when_running_with_content_type_validation_disabled : TestFixtureWithJsProjection {
			protected override void Given() {
				_projection = @"
                fromAll().when({$any: 
                    function(state, event) {
                    linkTo('output-stream' + event.sequenceNumber, event);
                    return {};
                }});
            ";
			}

			protected override IProjectionStateHandler CreateStateHandler() {
				return _stateHandlerFactory.Create(
					"JS", _projection, 
					enableContentTypeValidation: false, logger: (s, _) => {
						if (s.StartsWith("P:"))
							Console.WriteLine(s);
						else
							_logged.Add(s);
					}); // skip prelude debug output
			}

			[Test, Category("v8")]
			public void process_null_json_event_does_not_emit() {
				string state = null;
				EmittedEventEnvelope[] emittedEvents = null;

				var result = _stateHandler.ProcessEvent(
					"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
					"metadata",
					null, out state, out emittedEvents, isJson: true);

				Assert.IsNull(emittedEvents);
			}

			[Test, Category("v8")]
			public void process_null_non_json_event_does_not_emit() {
				string state = null;
				EmittedEventEnvelope[] emittedEvents = null;

				var result = _stateHandler.ProcessEvent(
					"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
					"metadata",
					null, out state, out emittedEvents, isJson: false);

				Assert.IsNull(emittedEvents);
			}
		}
	}
}

